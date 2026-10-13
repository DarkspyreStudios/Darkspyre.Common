using System.Diagnostics;

namespace Darkspyre.Common.Git;

/// <summary>Starts git from PATH with System.Diagnostics.Process. For consumers with no process provider of their own.</summary>
/// <gotcha>A stop request and a forced stop both kill the child and its descendants: .NET has no portable graceful stop.</gotcha>
public sealed class SystemGitProcessLauncher : IGitProcessLauncher
{
    /// <inheritdoc />
    public Task<IGitProcess> StartAsync(GitProcessStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo info = new("git")
        {
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in start.Arguments) info.ArgumentList.Add(argument);
        foreach ((string name, string? value) in start.Environment)
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }
        Process process = Process.Start(info) ?? throw new IOException("Git did not start.");
        return Task.FromResult<IGitProcess>(new SystemGitProcess(process));
    }

    private sealed class SystemGitProcess(Process process) : IGitProcess
    {
        public Stream StandardInput { get; } = process.StandardInput.BaseStream;

        public Stream StandardOutput { get; } = process.StandardOutput.BaseStream;

        public Stream StandardError { get; } = process.StandardError.BaseStream;

        public async Task<GitExit> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode);
        }

        public Task RequestStopAsync(CancellationToken cancellationToken) => this.ForceStopAsync(cancellationToken);

        public Task ForceStopAsync(CancellationToken cancellationToken)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // The child already exited.
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await this.ForceStopAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
        }
    }
}
