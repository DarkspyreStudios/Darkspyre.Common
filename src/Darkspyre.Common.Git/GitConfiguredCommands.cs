using System.Globalization;
using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>
/// Fetch, pull, push and the merge tool as the repository's Git configuration sets them up: remotes, refspecs, upstreams,
/// the pull integration policy, credential helpers and the configured merge tool. Each runs under the repository's
/// operation gate through <see cref="GitCommands.RunAsync"/>, with no timeout, and reports a
/// <see cref="GitCommandResult"/>. A failure the library recognizes gets its own sentence; any other carries Git's reason, as
/// <see cref="GitOperationChecks.Reason"/> takes it from Git's output.
/// </summary>
internal sealed class GitConfiguredCommands(GitCommands git, GitRepositoryReader reader, GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    private const string UnknownDiagnostic = "Git did not confirm the outcome. Refresh to inspect the repository before trying again.";
    private const string ToolDiagnostic = "Git's configured merge tool is unavailable. Check merge.guitool or merge.tool.";
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    /// <summary>Runs <c>git fetch</c> with the configured remote and refspecs.</summary>
    internal async Task<GitCommandResult> FetchAsync(string repository, CancellationToken cancellationToken)
    {
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(repository, cancellationToken).ConfigureAwait(false);
        using (operation)
            return await this.RunAsync(layout.WorktreePath, ["fetch"], "fetch", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <c>git pull --no-edit</c>, so Git's configured integration policy applies and a merge needs no editor.</summary>
    internal async Task<GitCommandResult> PullAsync(string repository, CancellationToken cancellationToken)
    {
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(repository, cancellationToken).ConfigureAwait(false);
        using (operation)
            return await this.RunAsync(layout.WorktreePath, ["pull", "--no-edit"], "pull", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <c>git push --porcelain</c>; a porcelain line starting with <c>!</c> is a rejection.</summary>
    internal async Task<GitCommandResult> PushAsync(string repository, CancellationToken cancellationToken)
    {
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(repository, cancellationToken).ConfigureAwait(false);
        using (operation)
            return await this.RunAsync(layout.WorktreePath, ["push", "--porcelain"], "push", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>git mergetool --gui --no-prompt</c> on one conflicted path. The path must be among the working state's
    /// unmerged paths and <c>merge.guitool</c> or <c>merge.tool</c> must be set; otherwise nothing starts. Git chooses and
    /// starts the tool, and standard input is closed, so a console question the tool asks cannot wait.
    /// </summary>
    internal async Task<GitCommandResult> ResolveAsync(GitFileActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path is null || !GitPaths.IsRelativePath(request.Path))
            throw new ArgumentException("Resolve needs a literal repository-relative path.", nameof(request));
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            if (!await GitRepositoryReader.PermitsAsync(this.paths, GitPaths.Combine(worktree, request.Path), cancellationToken).ConfigureAwait(false))
                throw new UnauthorizedAccessException("The path policy refuses reading the path.");
            GitWorkingState state = await this.reader.ReadWorkingStateAsync(worktree, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The worktree changed while waiting for its operation gate.");
            if (!state.ConflictedPaths.Contains(request.Path, StringComparer.Ordinal))
                return new(GitCommandState.Failed, GitFailure.MergeToolUnavailable, "The file has no conflict to resolve.");
            if (!await this.IsSetAsync(worktree, "merge.guitool", cancellationToken).ConfigureAwait(false)
                && !await this.IsSetAsync(worktree, "merge.tool", cancellationToken).ConfigureAwait(false))
                return new(GitCommandState.Failed, GitFailure.MergeToolUnavailable, ToolDiagnostic);
            return await this.RunAsync(worktree, ["--literal-pathspecs", "mergetool", "--gui", "--no-prompt", "--", request.Path],
                "resolve the conflict", cancellationToken).ConfigureAwait(false);
        }
    }

    // Locates the worktree root, checks once that the path policy permits writing the repository and the worktree, and
    // enters the repository's operation gate.
    private async Task<(GitRepositoryReader.GitLayout Layout, IDisposable Operation)> EnterAsync(string repository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        GitRepositoryReader.GitLayout layout = await this.reader.LocateAsync(repository, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The worktree is unavailable.");
        if (this.paths.Resolve(repository) != layout.WorktreePath)
            throw new ArgumentException("This Git operation requires the worktree root, not a subdirectory.", nameof(repository));
        await this.paths.RequireWriteAsync([layout.CommonDirectory, layout.WorktreePath], cancellationToken).ConfigureAwait(false);
        return (layout, await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false));
    }

    // Exit 1 with no output is Git's answer for a key that is not set.
    private async Task<bool> IsSetAsync(string worktree, string key, CancellationToken cancellationToken)
    {
        GitRunResult result = await this.git.RunReadAsync(worktree, ["config", "--get", key], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Exit is { ExitCode: 1, Diagnostic: null }) return false;
        GitOperationChecks.Check(result, "read the merge tool configuration");
        return true;
    }

    // Cancellation or a lost process after dispatch is Unknown; nothing is retried. Git's fixed English text, stable under
    // LC_ALL=C, classifies a failure where it identifies one.
    private async Task<GitCommandResult> RunAsync(string worktree, IReadOnlyList<string> arguments, string action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GitRunResult result;
        try { result = await this.git.RunAsync(worktree, arguments, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            return new(GitCommandState.Unknown, null, UnknownDiagnostic);
        }
        if (result.Exit.ExitCode is not { } code) return new(GitCommandState.Unknown, null, UnknownDiagnostic);
        string output = Encoding.UTF8.GetString(result.Output);
        string errors = Encoding.UTF8.GetString(result.Errors);
        if (arguments.Contains("--porcelain") && output.Split('\n').Any(line => line.StartsWith('!')))
            return new(GitCommandState.Failed, GitFailure.RemoteRejected, "The remote rejected this push. Fetch and review the branch state.");
        if (code == 0) return new(GitCommandState.Completed);
        string text = output + "\n" + errors;
        if (text.Contains("Authentication failed", StringComparison.Ordinal) || text.Contains("could not read Username", StringComparison.Ordinal))
            return new(GitCommandState.Failed, GitFailure.AuthenticationFailed, "Git could not authenticate with the configured remote.");
        if (text.Contains("has no upstream branch", StringComparison.Ordinal) || text.Contains("There is no tracking information", StringComparison.Ordinal))
            return new(GitCommandState.Failed, GitFailure.NoUpstream, "The current branch has no configured upstream.");
        if (arguments.Contains("mergetool") && text.Contains("is not available as", StringComparison.Ordinal))
            return new(GitCommandState.Failed, GitFailure.MergeToolUnavailable, ToolDiagnostic);
        return new(GitCommandState.Failed, GitFailure.CommandFailed, string.Create(CultureInfo.InvariantCulture,
            $"Git failed to {action} (exit {code}).") + (GitOperationChecks.Reason(result.Errors) is { } reason ? " " + reason : string.Empty)
            + " Refresh to inspect the repository.");
    }
}
