using System.Runtime.CompilerServices;
using Darkspyre.Common.TestSupport;

namespace Darkspyre.Common.Workspaces.Tests;

/// <summary>
/// Owns the per-process scratch root that holds every folder the tests create, and points the process's temporary
/// folder at it before any test code runs. The root is removed when the process exits.
/// </summary>
/// <remarks>
/// <c>AGENTDS_TEST_SCRATCH</c> names the parent folder. Without it, the parent is the <c>default-temp-location</c>
/// user setting (<see cref="TestDefaultTempLocation"/>), the same folder every agent run uses for temporary files.
/// With neither, the tests fail at start: there is no system-temporary fallback. The scratch root is
/// <c>&lt;parent&gt;/darkspyre-common-tests-&lt;process id&gt;</c>, so concurrent test runs never share or delete each other's
/// files. Test code allocates folders through <see cref="CreateDirectory"/>, never through the process's temporary
/// folder. <c>TMPDIR</c> is still set to the
/// root, so child processes the tests start and library code that reads it write beneath the same root.
/// </remarks>
public static class TestScratchRoot
{
    private const string Override = "AGENTDS_TEST_SCRATCH";

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> Releases = new();
    private static string? parent;
    private static string? root;

    /// <summary>The folder that holds each process's scratch root. Folders created here outlive the process.</summary>
    public static string Parent => parent ?? throw new InvalidOperationException("The test scratch root is not initialized.");

    /// <summary>This process's scratch root. It is deleted when the process exits.</summary>
    public static string Directory => root ?? throw new InvalidOperationException("The test scratch root is not initialized.");

    [ModuleInitializer]
    internal static void Initialize()
    {
        string? configured = Environment.GetEnvironmentVariable(Override);
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(!string.IsNullOrWhiteSpace(configured) ? configured
            : TestDefaultTempLocation.Read()
            ?? throw new InvalidOperationException(
                "Set AGENTDS_TEST_SCRATCH, or set the default-temp-location setting, to a folder outside every repository and the system temporary folder.")));
        string created = CreateNamed(parent, $"darkspyre-common-tests-{Environment.ProcessId}");
        root = created;
        Environment.SetEnvironmentVariable("TMPDIR", created + Path.DirectorySeparatorChar);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Exit(created);
    }

    /// <summary>
    /// Registers <paramref name="release"/> to run when the process exits, before the scratch root is removed. A helper
    /// that keeps something alive beneath the root for the whole process, such as a settings provider that watches its
    /// files and writes when they change, must release it here. Releasing it in its own exit handler races the removal.
    /// </summary>
    public static void OnExit(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Releases.Enqueue(release);
    }

    private static void Exit(string path)
    {
        while (Releases.TryDequeue(out Action? release))
        {
            try { release(); }
            catch (Exception error) { Console.Error.WriteLine($"Test scratch release failed: {error}"); }
        }
        Remove(path);
    }

    /// <summary>Creates a new owner-only folder named <paramref name="prefix"/> plus a random suffix in
    /// <paramref name="location"/>, or in the scratch root when it is omitted, and returns its full path.</summary>
    public static string CreateDirectory(string prefix, string? location = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        location ??= Directory;
        while (true)
        {
            string name = prefix + Guid.NewGuid().ToString("N")[..12];
            if (!Path.Exists(Path.Combine(location, name))) return CreateNamed(location, name);
        }
    }

    private static string CreateNamed(string location, string name)
    {
        string path = Path.Combine(location, name);
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>
    /// Deletes the root. A released watcher can still finish one write while the delete walks the tree, so a failed
    /// delete is retried a few times before the root is left for the next run's owner to see.
    /// </summary>
    private static void Remove(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    Console.Error.WriteLine($"Test scratch root {path} was not removed: {error.Message}");
                    return;
                }
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
