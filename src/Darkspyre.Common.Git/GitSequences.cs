using System.Globalization;
using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>
/// Rebase, cherry-pick and merge under the repository's operation gate. A start needs a worktree with no change other
/// than untracked files and no operation in progress; continue and abort need an operation of the same kind in progress,
/// and continue needs no unmerged paths. Git keeps every message and never starts an editor. After Git exits, the
/// working tree's state decides the result; a cancellation after dispatch is Unknown without reading anything.
/// </summary>
internal sealed class GitSequences(GitCommands git, GitRepositoryReader reader, GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    // A cherry-pick names at most 64 commits; a larger replay belongs in a rebase.
    private const int CommitLimit = 64;
    private const string Rebase = "rebase";
    private const string CherryPick = "cherry-pick";
    private const string Merge = "merge";
    // A NotClean reason names at most this many changed paths.
    private const int NamedChanges = 5;
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    /// <summary>
    /// Starts a rebase of the worktree onto the resolved upstream with <c>-c rebase.updateRefs=false</c> and
    /// <c>--rebase-merges --no-autostash --no-autosquash --no-rerere-autoupdate</c>, or continues or aborts the rebase in
    /// progress.
    /// </summary>
    internal async Task<GitSequenceResult> RebaseAsync(GitRebaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAction(request.Action);
        if (request.Action == GitSequenceAction.Start && string.IsNullOrWhiteSpace(request.Upstream))
            throw new ArgumentException("A rebase start needs the upstream to rebase onto.", nameof(request));
        (IDisposable operation, GitWorkingState state) = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        using IDisposable entered = operation;
        string? onto;
        string[] arguments;
        if (request.Action == GitSequenceAction.Start)
        {
            await this.RequireStartAsync(state, cancellationToken).ConfigureAwait(false);
            onto = await this.reader.ResolveCommitAsync(state.Worktree, request.Upstream!, cancellationToken).ConfigureAwait(false);
            // --no-rerere-autoupdate keeps rerere.autoUpdate from staging paths the caller did not name.
            // rebase.updateRefs=false keeps the rebase from moving other branches; Git before 2.38 ignores the key.
            arguments = ["-c", "rebase.updateRefs=false", "rebase", "--rebase-merges", "--no-autostash", "--no-autosquash",
                "--no-rerere-autoupdate", onto];
        }
        else
        {
            RequireInProgress(state, Rebase, request.Action);
            // The rebase's own base, read before Git moves on, so its new commits are counted from where it started.
            onto = request.Action == GitSequenceAction.Continue ? ReadOnto(state) : null;
            arguments = ["rebase", request.Action == GitSequenceAction.Continue ? "--continue" : "--abort"];
        }
        return await this.DispatchAsync(new(state, Rebase, request.Action, onto, []), arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a cherry-pick of 1 to 64 resolved commits in the given order with <c>--no-rerere-autoupdate</c>, or continues
    /// or aborts the cherry-pick in progress. A merge commit or one that starts empty is refused before anything changes.
    /// A source commit whose changes are already in HEAD stops the pick with nothing to commit; it is skipped and reported
    /// as skipped.
    /// </summary>
    internal async Task<GitSequenceResult> CherryPickAsync(GitCherryPickRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Commits);
        RequireAction(request.Action);
        if (request.Action == GitSequenceAction.Start && request.Commits.Count is 0 or > CommitLimit)
            throw new ArgumentException("A cherry-pick start names 1 to 64 commits.", nameof(request));
        (IDisposable operation, GitWorkingState state) = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        using IDisposable entered = operation;
        IReadOnlyList<string> sources;
        string[] arguments;
        if (request.Action == GitSequenceAction.Start)
        {
            await this.RequireStartAsync(state, cancellationToken).ConfigureAwait(false);
            sources = await this.reader.ResolveCommitsAsync(state.Worktree, request.Commits, cancellationToken).ConfigureAwait(false);
            foreach (string commit in sources)
                await this.RequireOrdinaryAsync(state, commit, cancellationToken).ConfigureAwait(false);
            // Git applies explicitly named commits in command-line order; one already in HEAD stops the pick, which the
            // dispatch skips.
            arguments = ["cherry-pick", "--no-rerere-autoupdate", .. sources];
        }
        else
        {
            RequireInProgress(state, CherryPick, request.Action);
            sources = request.Action == GitSequenceAction.Continue
                ? await this.ReadRemainingPicksAsync(state, cancellationToken).ConfigureAwait(false) : [];
            arguments = ["cherry-pick", request.Action == GitSequenceAction.Continue ? "--continue" : "--abort"];
        }
        // A cherry-pick only appends, so the commits after HEAD as it is now are exactly what this call creates.
        string? since = request.Action == GitSequenceAction.Abort ? null : state.Head;
        return await this.DispatchAsync(new(state, CherryPick, request.Action, since, sources), arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a merge of the resolved source into HEAD with <c>--no-ff --no-edit</c> and the given message, or continues
    /// or aborts the merge in progress. A conflict leaves the merge in progress.
    /// </summary>
    internal async Task<GitSequenceResult> MergeAsync(GitMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAction(request.Action);
        if (request.Action == GitSequenceAction.Start && (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Message)
            || request.Message.Contains('\0')))
            throw new ArgumentException("A merge start needs the source to merge and a message without null characters.", nameof(request));
        (IDisposable operation, GitWorkingState state) = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        using IDisposable entered = operation;
        string[] arguments;
        if (request.Action == GitSequenceAction.Start)
        {
            await this.RequireStartAsync(state, cancellationToken).ConfigureAwait(false);
            string source = await this.reader.ResolveCommitAsync(state.Worktree, request.Source!, cancellationToken).ConfigureAwait(false);
            arguments = ["merge", "--no-ff", "--no-edit", "-m", request.Message!, source];
        }
        else
        {
            RequireInProgress(state, Merge, request.Action);
            arguments = ["merge", request.Action == GitSequenceAction.Continue ? "--continue" : "--abort"];
        }
        // A merge in progress has not moved HEAD, so the merge commit is the first-parent commit after HEAD as it is now.
        string? since = request.Action == GitSequenceAction.Abort ? null : state.Head;
        return await this.DispatchAsync(new(state, Merge, request.Action, since, []), arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    // Locates the worktree root, checks once that the scope writes the repository and the worktree, enters the
    // repository's operation gate and reads the working state under it.
    private async Task<(IDisposable Operation, GitWorkingState State)> EnterAsync(string repository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        GitRepositoryReader.GitLayout layout = await this.reader.LocateAsync(repository, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The worktree is unavailable.");
        if (this.paths.Resolve(repository) != layout.WorktreePath)
            throw new ArgumentException("A rebase, cherry-pick or merge requires the worktree root, not a subdirectory.", nameof(repository));
        await this.paths.RequireWriteAsync([layout.CommonDirectory, layout.WorktreePath], cancellationToken).ConfigureAwait(false);
        IDisposable operation = await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            GitWorkingState state = await this.reader.ReadWorkingStateAsync(layout.WorktreePath, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The worktree changed while waiting for its operation gate.");
            return (operation, state);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    // A start needs no operation in progress and no change but untracked files.
    private async Task RequireStartAsync(GitWorkingState state, CancellationToken cancellationToken)
    {
        if (state.OperationKind is { } running)
            throw new GitOperationException(GitFailure.NotClean, $"A {running} is in progress; finish or abort it first. Nothing changed.");
        // Untracked files stay allowed: Git refuses on its own when one would be overwritten.
        string[] changed = [.. state.Changes.Where(change => change.IndexStatus != '?').Select(change => change.Path)];
        if (changed.Length == 0) return;
        IReadOnlyList<string> named = await this.reader.PermittedAsync(state.Worktree, changed.Take(NamedChanges), cancellationToken)
            .ConfigureAwait(false);
        throw new GitOperationException(GitFailure.NotClean, named.Count == 0
            ? "The index or worktree has uncommitted changes. Nothing changed."
            : $"The index or worktree has uncommitted changes, such as {string.Join(", ", named)}. Nothing changed.");
    }

    // Continue and abort reach only an operation of their own kind; continue with unmerged paths runs nothing.
    private static void RequireInProgress(GitWorkingState state, string kind, GitSequenceAction action)
    {
        if (state.OperationKind != kind)
            throw new GitOperationException(GitFailure.StaleState, $"No {kind} is in progress; read the status again. Nothing changed.");
        if (action == GitSequenceAction.Continue && state.ConflictedPaths.Count != 0)
            throw new GitOperationException(GitFailure.NotClean,
                $"The {kind} has unmerged paths; resolve and stage them before continuing. Nothing was run.");
    }

    // A merge commit, or one that changes nothing against its parent (or, for a root commit, the empty tree), is refused.
    private async Task RequireOrdinaryAsync(GitWorkingState state, string commit, CancellationToken cancellationToken)
    {
        string[] listed = Lines(await this.reader.ReadAsync(state.Worktree, ["rev-list", "--parents", "-n", "1", commit, "--"],
            cancellationToken).ConfigureAwait(false));
        if (listed.Length != 1) throw new InvalidDataException("Git returned an invalid commit listing.");
        string[] parents = listed[0].Split(' ')[1..];
        if (parents.Length > 1)
            throw new ArgumentException($"Commit {Short(commit)} is a merge commit; a cherry-pick applies ordinary commits only.", nameof(commit));
        string[] trees = Lines(await this.reader.ReadAsync(state.Worktree,
            parents.Length == 0 ? ["rev-parse", commit + "^{tree}"] : ["rev-parse", commit + "^{tree}", commit + "^1^{tree}"],
            cancellationToken).ConfigureAwait(false));
        if (trees.Length != parents.Length + 1) throw new InvalidDataException("Git returned invalid commit trees.");
        string before = parents.Length == 0 ? EmptyTree(state.ObjectFormat) : trees[1];
        if (string.Equals(trees[0], before, StringComparison.Ordinal))
            throw new ArgumentException($"Commit {Short(commit)} starts empty; a cherry-pick applies commits that change files.", nameof(commit));
    }

    private async Task<GitSequenceResult> DispatchAsync(Dispatch dispatch, string[] arguments,
        CancellationToken cancellationToken)
    {
        GitRunResult result;
        try
        {
            result = await this.git.RunSequenceAsync(dispatch.Before.Worktree, arguments, cancellationToken).ConfigureAwait(false);
            if (dispatch.Kind == CherryPick && dispatch.Action != GitSequenceAction.Abort)
                result = await this.SkipAppliedPicksAsync(dispatch, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // After dispatch, a cancellation or a lost child does not establish what Git did. Nothing is read or retried.
            return Unknown(dispatch.Kind);
        }
        try { return await this.ClassifyAsync(dispatch, result, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Unknown(dispatch.Kind); }
    }

    // A pick whose changes are already in HEAD stops with nothing to commit: CHERRY_PICK_HEAD exists, no path is unmerged
    // and the index matches HEAD. Each such stop is skipped, at most once per source, since each skip consumes one pick.
    // A stop with staged changes, such as one a hook refused, stays stopped. The last command's result is returned.
    private async Task<GitRunResult> SkipAppliedPicksAsync(Dispatch dispatch, GitRunResult result,
        CancellationToken cancellationToken)
    {
        string worktree = dispatch.Before.Worktree;
        for (int skips = 0; skips < dispatch.Sources.Count; skips++)
        {
            if (await this.reader.ReadWorkingStateAsync(worktree, cancellationToken).ConfigureAwait(false) is not { } state
                || !File.Exists(state.OperationPaths.CherryPickHead))
                break;
            byte[] unmerged = await this.reader.ReadAsync(worktree, ["ls-files", "-u", "-z"], cancellationToken).ConfigureAwait(false);
            if (unmerged.Length != 0) break;
            GitRunResult staged = await this.git.RunReadAsync(worktree, ["diff", "--cached", "--quiet"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (staged.Exit is not { ExitCode: 0, Diagnostic: null })
            {
                if (staged.Exit is not { ExitCode: 1, Diagnostic: null }) Check(staged, "read the staged changes");
                break;
            }
            result = await this.git.RunSequenceAsync(worktree, ["cherry-pick", "--skip"], cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    // The working tree after Git exits decides: an operation of this kind still in progress is Conflicted with unmerged
    // paths and Stopped without; none and a clean exit is Completed, or Aborted for an abort; a failed exit with nothing
    // in progress and HEAD and the working tree's changes unchanged is Refused; anything else is Unknown.
    private async Task<GitSequenceResult> ClassifyAsync(Dispatch dispatch, GitRunResult result,
        CancellationToken cancellationToken)
    {
        GitWorkingState before = dispatch.Before;
        // A worktree that is gone afterwards fits no other state.
        if (await this.reader.ReadWorkingStateAsync(before.Worktree, cancellationToken).ConfigureAwait(false) is not { } after)
            return Unknown(dispatch.Kind);
        bool exited = result.Exit.ExitCode == 0 && result.Exit.Diagnostic is null;
        GitSequenceState state;
        if (after.OperationKind is { } running)
            state = running != dispatch.Kind ? GitSequenceState.Unknown
                : after.ConflictedPaths.Count != 0 ? GitSequenceState.Conflicted : GitSequenceState.Stopped;
        else if (exited) state = dispatch.Action == GitSequenceAction.Abort ? GitSequenceState.Aborted : GitSequenceState.Completed;
        // GitChange is a record, so equal changes have the same paths and both status characters.
        else state = after.Head == before.Head && after.Changes.SequenceEqual(before.Changes) ? GitSequenceState.Refused : GitSequenceState.Unknown;
        bool progressed = state is GitSequenceState.Completed or GitSequenceState.Conflicted or GitSequenceState.Stopped;
        // A merge adds one commit on the first-parent line; the source's own commits are not new.
        IReadOnlyList<string> created = progressed && dispatch.Since is { } since
            ? Lines(await this.reader.ReadAsync(before.Worktree, dispatch.Kind == Merge
                    ? ["rev-list", "--reverse", "--first-parent", since + "..HEAD", "--"]
                    : ["rev-list", "--reverse", since + "..HEAD", "--"], cancellationToken)
                .ConfigureAwait(false))
            : [];
        IReadOnlyList<string> skipped = progressed && dispatch.Kind == CherryPick
            ? await this.ReadSkippedAsync(after, dispatch.Sources, created, cancellationToken).ConfigureAwait(false)
            : [];
        IReadOnlyList<string> conflicted = await this.reader.PermittedAsync(after.Worktree, after.ConflictedPaths, cancellationToken)
            .ConfigureAwait(false);
        return new(after.Head, after.BranchRef, conflicted, state, created, Explain(dispatch.Kind, state)) { SkippedCommits = skipped };
    }

    // A source the sequence has not reached is not skipped: while it is stopped, only the sources before the commit it
    // stopped at were processed. A processed source is skipped when its stable patch ID matches no commit this call created.
    private async Task<IReadOnlyList<string>> ReadSkippedAsync(GitWorkingState after, IReadOnlyList<string> sources,
        IReadOnlyList<string> created, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> processed = sources;
        if (after.OperationKind is not null)
        {
            int stopped = ReadHead(after.OperationPaths.CherryPickHead, after.ObjectFormat) is { } current
                ? sources.ToList().FindIndex(source => string.Equals(source, current, StringComparison.OrdinalIgnoreCase)) : -1;
            processed = stopped < 0 ? [] : [.. sources.Take(stopped)];
        }
        if (processed.Count == 0) return [];
        Dictionary<string, string> patches = await this.ReadPatchIdsAsync(after.Worktree, [.. processed, .. created], cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> applied = [.. created.Where(patches.ContainsKey).Select(commit => patches[commit])];
        return [.. processed.Where(source => !patches.TryGetValue(source, out string? patch) || !applied.Contains(patch))];
    }

    // One log of the commits' patches feeds patch-id --stable, whose lines are "<patch ID> <commit>".
    private async Task<Dictionary<string, string>> ReadPatchIdsAsync(string worktree, IReadOnlyList<string> commits,
        CancellationToken cancellationToken)
    {
        byte[] patches = await this.reader.ReadAsync(worktree,
            ["log", "--no-walk=unsorted", "-p", "--no-color", "--no-ext-diff", "--no-textconv", "--no-decorate", "--no-show-signature",
                "--format=commit %H", .. commits.Distinct(StringComparer.Ordinal), "--"], cancellationToken).ConfigureAwait(false);
        GitRunResult identified = await this.git.RunReadAsync(worktree, ["patch-id", "--stable"],
            input: patches, cancellationToken: cancellationToken).ConfigureAwait(false);
        Check(identified, "read patch IDs");
        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(identified.Output))
        {
            string[] fields = line.Split(' ');
            if (fields.Length != 2) throw new InvalidDataException("Git returned an invalid patch ID.");
            ids[fields[1]] = fields[0];
        }
        return ids;
    }

    // The picks a continue will process after the commit the sequence stopped at, which continuing commits and so never
    // drops as already applied. A single-commit pick has no sequencer and no further picks.
    private async Task<IReadOnlyList<string>> ReadRemainingPicksAsync(GitWorkingState state,
        CancellationToken cancellationToken)
    {
        string todo = Path.Combine(state.OperationPaths.Sequencer, "todo");
        if (!File.Exists(todo)) return [];
        List<string> named = [];
        foreach (string line in await File.ReadAllLinesAsync(todo, cancellationToken).ConfigureAwait(false))
        {
            string[] words = line.Split(' ', 3);
            if (words.Length >= 2 && words[0] == "pick") named.Add(words[1]);
        }
        if (named.Count == 0) return [];
        IReadOnlyList<string> picks = await this.reader.ResolveCommitsAsync(state.Worktree, named, cancellationToken)
            .ConfigureAwait(false);
        string? current = ReadHead(state.OperationPaths.CherryPickHead, state.ObjectFormat);
        return current is not null && string.Equals(picks[0], current, StringComparison.OrdinalIgnoreCase) ? [.. picks.Skip(1)] : picks;
    }

    private static string? ReadOnto(GitWorkingState state)
    {
        string onto = Path.Combine(state.OperationPaths.RebaseMerge, "onto");
        return File.Exists(onto) && File.ReadAllText(onto).TrimEnd('\n') is var text && GitPaths.IsObjectId(text, state.ObjectFormat)
            ? text : null;
    }

    private static string? ReadHead(string path, GitObjectFormat format) =>
        File.Exists(path) && File.ReadAllText(path).TrimEnd('\n') is var text && GitPaths.IsObjectId(text, format) ? text : null;

    private static void RequireAction(GitSequenceAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown sequence action.", nameof(action));
    }

    private static GitSequenceResult Unknown(string kind) => new(null, null, [], GitSequenceState.Unknown, [],
        $"The outcome of the {kind} is unknown; inspect the status before any retry.");

    private static string? Explain(string kind, GitSequenceState state) => state switch
    {
        GitSequenceState.Conflicted => $"The {kind} stopped with conflicts; resolve and stage them, then continue, or abort.",
        GitSequenceState.Stopped => $"The {kind} stopped without conflicts, for example because a hook failed; inspect the status, then continue or abort.",
        GitSequenceState.Refused => $"Git or a hook refused the {kind}; nothing changed.",
        GitSequenceState.Unknown => $"The state after the {kind} is not one the host recognizes; inspect the status before any retry.",
        _ => null,
    };

    private static string EmptyTree(GitObjectFormat format) => format == GitObjectFormat.Sha256
        ? "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321"
        : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private static string Short(string commit) => commit[..Math.Min(12, commit.Length)];

    private static string[] Lines(byte[] output)
    {
        string text = Encoding.UTF8.GetString(output);
        if (text.Length != 0 && !text.EndsWith('\n')) throw new InvalidDataException("Git returned an incomplete line.");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>One dispatched call: the state before it, the operation kind, the action, the commit new commits are counted
    /// from, and the source commits a cherry-pick processes.</summary>
    private sealed record Dispatch(GitWorkingState Before, string Kind, GitSequenceAction Action, string? Since,
        IReadOnlyList<string> Sources);
}
