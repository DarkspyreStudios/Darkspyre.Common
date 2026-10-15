using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Family-wide launch accounting shared by independent applications. The permanent launch lock serializes
/// reservations; only the permanent owner lock proves ownership. Metadata never authorizes lock stealing.
/// </summary>
internal sealed class ServiceLaunchBudget : IDisposable
{
    private const string FileName = "launches.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };
    private readonly ServiceDirectory runtime;
    private readonly TimeProvider time;

    public ServiceLaunchBudget(ServiceFileSystem fileSystem, string runtimePath, TimeProvider? timeProvider = null)
    {
        this.runtime = fileSystem.OpenPrivateDirectory(runtimePath);
        this.time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ServiceLaunchReservation> TryReserveAsync(CancellationToken cancellationToken = default)
    {
        using IServicePackageLease gate = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        using IServicePackageLease? owner = this.runtime.TryAcquire("owner.lock");
        if (owner is null) return new(ServiceLaunchDecision.OwnerPresent);
        List<Attempt> attempts = Read();
        long now = this.time.GetUtcNow().ToUnixTimeMilliseconds();
        // UTC is comparable across processes and persisted across launches. A backwards clock fails closed
        // until the recorded window passes or the user explicitly resets the budget. Lease time stays monotonic.
        attempts.RemoveAll(attempt => now - attempt.Started >= ServiceProtocol.LaunchWindow.TotalMilliseconds);
        if (attempts.Any(attempt => attempt.Pending && now - attempt.Started < ServiceProtocol.FirstSubscriberDeadline.TotalMilliseconds))
            return new(ServiceLaunchDecision.Starting);
        if (attempts.Count >= ServiceProtocol.MaximumLaunchAttempts) return new(ServiceLaunchDecision.Exhausted);
        Guid id = Guid.NewGuid();
        attempts.Add(new(id, now, true));
        Write(attempts);
        return new(ServiceLaunchDecision.Reserved, id);
    }

    /// <summary>The winning backend calls this under actual family/store ownership, before publishing readiness.</summary>
    public Task MarkRunningAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        UpdateAsync(attemptId, normalExit: false, cancellationToken);

    /// <summary>
    /// The backend records normal cleanup while it still owns the family. Its launching application's exit
    /// observer also records the outcome. Updates are idempotent, including an expired or explicitly reset attempt.
    /// </summary>
    public Task RecordExitAsync(Guid attemptId, bool normalExit, CancellationToken cancellationToken = default) =>
        UpdateAsync(attemptId, normalExit, cancellationToken);

    /// <summary>Explicit user retry only. This resets accounting, never an owner's locks, files or process.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using IServicePackageLease gate = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        _ = Read(); // Do not silently replace malformed/untrusted metadata.
        Write([]);
    }

    private async Task UpdateAsync(Guid id, bool normalExit, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) throw new ArgumentException("A launch attempt identity is required.", nameof(id));
        using IServicePackageLease gate = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        List<Attempt> attempts = Read();
        int index = attempts.FindIndex(attempt => attempt.Id == id);
        if (index < 0) return;
        if (normalExit) attempts.RemoveAt(index);
        else attempts[index] = attempts[index] with { Pending = false };
        Write(attempts);
    }

    private List<Attempt> Read()
    {
        byte[]? bytes = this.runtime.ReadPrivateFile(FileName);
        if (bytes is null) return [];
        Ledger? ledger;
        try { ledger = JsonSerializer.Deserialize<Ledger>(bytes, Json); }
        catch (JsonException error) { throw new InvalidDataException("The launch ledger is malformed.", error); }
        if (ledger is null || ledger.Format != 1 || ledger.Attempts is null
            || ledger.Attempts.Length > ServiceProtocol.MaximumLaunchAttempts
            || ledger.Attempts.Any(attempt => attempt is null || attempt.Id == Guid.Empty || attempt.Started < 0 || attempt.Started > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            || ledger.Attempts.Select(attempt => attempt.Id).Distinct().Count() != ledger.Attempts.Length)
            throw new InvalidDataException("The launch ledger is invalid.");
        return [.. ledger.Attempts];
    }

    private void Write(List<Attempt> attempts) => this.runtime.PublishPrivateFile(FileName,
        JsonSerializer.SerializeToUtf8Bytes(new Ledger(1, [.. attempts]), Json));

    private async Task<IServicePackageLease> AcquireAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ServiceProtocol.ConnectDeadline);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (this.runtime.TryAcquire("launch.lock") is { } lease) return lease;
            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
        }
    }

    public void Dispose() => this.runtime.Dispose();

    private sealed record Attempt(Guid Id, long Started, bool Pending);
    private sealed record Ledger(int Format, Attempt[] Attempts);
}
