using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

// Private metadata uses the existing no-follow, owner-only, atomic publication boundary. Registration is unchanged.
internal static class ServiceStartupFiles
{
    private const string StateFile = "startup-state.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false };
    private static readonly JsonSerializerOptions IdentityJson = new(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string IdentityFile(Guid attempt) => $"{attempt:N}.identity.json";
    private static string LogFile(Guid attempt) => $"startup-{attempt:D}.log";
    private static string FailureFile(Guid attempt) => $"startup-{attempt:D}.json";
    internal static string OutputPath(string runtime, Guid attempt) => Path.Combine(runtime, LogFile(attempt));

    internal static string PrepareOutput(ServiceFileSystem files, ServiceRegistration registration, Guid attempt, string digest)
    {
        using var deadline = new CancellationTokenSource(ServiceProtocol.ConnectDeadline);
        using var cache = ServicePackageCache.OpenExisting(files, registration.PackageCachePath);
        // Launch is the existing synchronous native boundary. Its metadata uses the existing transport-setup bound.
        using IServicePackageLease catalog = cache.AcquireCatalogAsync(deadline.Token).GetAwaiter().GetResult();
        using ServiceDirectory runtime = files.OpenPrivateDirectory(registration.RuntimePath);
        using ServiceDirectory identities = files.OpenPrivateDirectory(Path.Combine(registration.RuntimePath, "attempts"));
        return PrepareOutput(runtime, identities, attempt, digest, DateTimeOffset.UtcNow);
    }
    internal static string PrepareOutput(ServiceDirectory runtime, ServiceDirectory identities, Guid attempt, string digest, DateTimeOffset started)
    {
        if (attempt == Guid.Empty) throw new ArgumentException("A startup attempt is required.", nameof(attempt));
        ServicePackageManifest.ValidateDigest(digest);
        runtime.PublishPrivateFile(LogFile(attempt), "\n"u8);
        identities.PublishPrivateFile(IdentityFile(attempt), JsonSerializer.SerializeToUtf8Bytes(new Attempt(attempt, digest, started, false, null), IdentityJson));
        return OutputPath(runtime.Path, attempt);
    }

    // This wraps only the native Completion, not launch-budget accounting or the backend's pre-exit CompleteAsync.
    internal static async Task<ServiceProcessExit> ObserveExitAsync(ServiceFileSystem files, ServiceRegistration registration,
        Guid attempt, Task<ServiceProcessExit> completion)
    {
        ServiceProcessExit exit = await completion.ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(ServiceProtocol.ConnectDeadline);
            using var cache = ServicePackageCache.OpenExisting(files, registration.PackageCachePath);
            using IServicePackageLease catalog = await cache.AcquireCatalogAsync(deadline.Token).ConfigureAwait(false);
            using ServiceDirectory? runtime = files.OpenExistingPrivateDirectory(registration.RuntimePath);
            using ServiceDirectory? identities = files.OpenExistingPrivateDirectory(Path.Combine(registration.RuntimePath, "attempts"));
            if (runtime is not null && identities is not null) ObserveExit(runtime, identities, attempt, exit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            // A diagnostic cleanup failure leaves its identity for retry and never replaces the actual process result.
            Trace.TraceWarning("The ended service attempt's diagnostic files could not be reconciled.");
        }
        return exit;
    }
    internal static void ObserveExit(ServiceDirectory runtime, ServiceDirectory identities, Guid attempt, ServiceProcessExit exit)
    {
        Attempt? identity = ReadIdentity(identities, IdentityFile(attempt));
        if (identity is null) return;
        identity = identity with { ExitObserved = true, NormalExit = exit.ExitCode == 0 };
        identities.PublishPrivateFile(IdentityFile(attempt), JsonSerializer.SerializeToUtf8Bytes(identity, IdentityJson));
        Collect(runtime, identities);
    }
    private static void Collect(ServiceDirectory runtime, ServiceDirectory identities)
    {
        List<(Attempt Identity, ServiceStartupFailure? Failure)> ended = [];
        foreach (string path in Directory.EnumerateFiles(identities.Path, "*.identity.json", SearchOption.TopDirectoryOnly))
        {
            Attempt? identity = ReadIdentity(identities, Path.GetFileName(path));
            if (identity is not { ExitObserved: true }) continue;
            ServiceStartupFailure? failure = null;
            try { failure = ReadFailure(runtime, identity.AttemptId); }
            catch (JsonException) { } // A parsed failure does not exist in malformed JSON.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; } // Unreadable is unknown, not absent.
            if (failure?.BuildDigest != identity.BuildDigest) failure = null;
            if (identity.NormalExit == true && failure is null) Remove(runtime, identities, identity);
            else ended.Add((identity, failure)); // A valid typed failure wins even when the process returned zero.
        }
        foreach (var group in ended.GroupBy(item => item.Identity.BuildDigest, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(item => item.Identity.StartedUtc).ThenByDescending(item => item.Identity.AttemptId).ToArray();
            var newest = ordered[0];
            // A missing/malformed summary or missing private output cannot justify discarding an older complete failure.
            if (newest.Failure is null || !runtime.HasPrivateFile(LogFile(newest.Identity.AttemptId))) continue;
            foreach (var older in ordered.Skip(1)) Remove(runtime, identities, older.Identity);
        }
    }
    private static Attempt? ReadIdentity(ServiceDirectory identities, string file)
    {
        if (!file.EndsWith(".identity.json", StringComparison.Ordinal)
            || !Guid.TryParseExact(file.AsSpan(0, file.Length - ".identity.json".Length), "N", out Guid named)) return null;
        try
        {
            byte[]? bytes = identities.ReadPrivateFile(file);
            Attempt? value = bytes is null ? null : JsonSerializer.Deserialize<Attempt>(bytes, IdentityJson);
            return value is not null && value.AttemptId == named && named != Guid.Empty && value.StartedUtc != default
                && value.BuildDigest is { Length: 64 } && value.BuildDigest.All(Uri.IsHexDigit)
                && value.ExitObserved == (value.NormalExit is not null) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static void Remove(ServiceDirectory runtime, ServiceDirectory identities, Attempt identity)
    {
        runtime.RemovePrivateFile(LogFile(identity.AttemptId));
        runtime.RemovePrivateFile(FailureFile(identity.AttemptId));
        identities.RemovePrivateFile(IdentityFile(identity.AttemptId)); // Last: a partial cleanup remains attributable.
    }

    internal static ServiceStartupState? ReadState(ServiceDirectory root)
    {
        byte[]? bytes = root.ReadPrivateFile(StateFile);
        if (bytes is null) return null;
        State state = JsonSerializer.Deserialize<State>(bytes, Json) ?? throw new InvalidDataException("The startup input is missing.");
        if (state.Id == Guid.Empty) throw new InvalidDataException("The startup input has no operation identity.");
        return new(state.Id, Convert.FromBase64String(state.Value));
    }
    internal static void PublishState(ServiceDirectory root, ServiceStartupState state)
    {
        if (state.Id == Guid.Empty) throw new ArgumentException("A startup operation is required.", nameof(state));
        root.PublishPrivateFile(StateFile, JsonSerializer.SerializeToUtf8Bytes(new State(state.Id, Convert.ToBase64String(state.Value.Span)), Json));
    }
    internal static void ClearState(ServiceDirectory root, Guid id)
    { if (ReadState(root)?.Id == id) root.RemovePrivateFile(StateFile); }

    internal static async Task ClearStateAsync(ServiceFileSystem files, string cachePath, string runtime, Guid id, CancellationToken token)
    {
        using var cache = ServicePackageCache.OpenExisting(files, cachePath);
        using IServicePackageLease catalog = await cache.AcquireCatalogAsync(token).ConfigureAwait(false);
        using ServiceDirectory root = files.OpenExistingPrivateDirectory(Path.GetDirectoryName(runtime)!)
            ?? throw new ServiceProtocolException(ServiceErrorCode.NotInstalled);
        ClearState(root, id);
    }
    internal static Task WriteFailureAsync(ServiceFileSystem files, ServiceStart start, ServiceStartupFailure failure, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using ServiceDirectory directory = files.OpenExistingPrivateDirectory(start.RuntimePath)
            ?? throw new ServiceProtocolException(ServiceErrorCode.NotInstalled);
        WriteFailure(directory, start, failure);
        return Task.CompletedTask;
    }
    internal static void WriteFailure(ServiceDirectory directory, ServiceStart start, ServiceStartupFailure failure)
    {
        if (start.AttemptId == Guid.Empty || directory.Path != start.RuntimePath || failure.AttemptId != start.AttemptId
            || failure.BuildDigest != start.BuildDigest || failure.OutputPath != OutputPath(start.RuntimePath, start.AttemptId))
            throw new ArgumentException("The startup summary does not match its attempt.", nameof(failure));
        ServicePackageManifest.ValidateDigest(start.BuildDigest);
        directory.PublishPrivateFile(FailureFile(start.AttemptId), JsonSerializer.SerializeToUtf8Bytes(failure, Json));
    }
    internal static Task<ServiceStartupFailure?> ReadFailureAsync(ServiceFileSystem files, string runtime, Guid attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using ServiceDirectory? directory = files.OpenExistingPrivateDirectory(runtime);
        return Task.FromResult(directory is null ? null : ReadFailure(directory, attempt));
    }
    private static ServiceStartupFailure? ReadFailure(ServiceDirectory directory, Guid attempt)
    {
        byte[]? bytes = directory.ReadPrivateFile(FailureFile(attempt));
        ServiceStartupFailure? failure = bytes is null ? null : JsonSerializer.Deserialize<ServiceStartupFailure>(bytes, Json);
        if (failure is not null && (failure.AttemptId != attempt || failure.OutputPath != OutputPath(directory.Path, attempt)))
            throw new InvalidDataException("The startup summary does not match its attempt.");
        return failure is null ? null : failure with { OwnerEvidence = failure.OwnerEvidence?.ToArray() };
    }
    private sealed record State(Guid Id, string Value);
    private sealed record Attempt(Guid AttemptId, string BuildDigest, DateTimeOffset StartedUtc, bool ExitObserved, bool? NormalExit);
}
