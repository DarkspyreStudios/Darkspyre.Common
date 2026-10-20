using System.Diagnostics;
using Darkspyre.Common.Git;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Darkspyre.Common.Workspaces.Tests;

/// <summary>DSA-1028: workspace folders against real temporary Git repositories.</summary>
public sealed class WorkspaceFoldersTests
{
    private static readonly WorkspaceActor Actor = new("session 1234", "test");

    /// <summary>A creation fetches each base, puts each worktree on the branch, locks it, makes tmp/home and logs each step.</summary>
    [Fact]
    public async Task CreateMakesLockedWorktreesOnTheBranchAndTmpHome()
    {
        Fixture fixture = Fixture.Create();
        string published = fixture.PublishToOrigin(fixture.First, "published.txt");

        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(new(fixture.Folder, "dsa-1/work", fixture.Repositories, false), Actor);

        Assert.Equal(fixture.Folder, created.Folder);
        Assert.Empty(created.Skipped);
        Assert.Equal(["first", "second"], created.Worktrees.Select(item => item.Repository.Name));
        Assert.All(created.Worktrees, item => Assert.Equal("dsa-1/work", Git.Run(item.Path, "branch", "--show-current").Trim()));
        Assert.All(created.Worktrees, item => Assert.True(Locked(item.Path)));
        Assert.Equal(published, Git.Run(created.Worktrees[0].Path, "rev-parse", "HEAD").Trim());
        Assert.True(Directory.Exists(Path.Combine(fixture.Folder, "tmp", "home")));
        Assert.Contains(fixture.Log.Lines, line => line.Contains("Locking the worktree", StringComparison.Ordinal)
            && line.Contains("for the test of session 1234", StringComparison.Ordinal));
    }

    /// <summary>A removal unlocks each locked worktree, removes it, deletes its branch and deletes the folder.</summary>
    [Fact]
    public async Task RemoveRemovesALockedWorkspaceWithItsBranches()
    {
        Fixture fixture = Fixture.Create();
        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(new(fixture.Folder, "done", fixture.Repositories, false), Actor);
        Directory.CreateDirectory(Path.Combine(fixture.Folder, "tmp", "home", "cache"));

        WorkspaceRemovalResult removed = await fixture.Folders.RemoveAsync(fixture.Folder, Actor);

        Assert.Equal(created.Worktrees.Select(item => item.Path), removed.Worktrees);
        Assert.Equal(["refs/heads/done", "refs/heads/done"], removed.Branches);
        Assert.False(Directory.Exists(fixture.Folder));
        Assert.Empty(Git.Run(fixture.First, "branch", "--list", "done").Trim());
        Assert.Empty(Git.Run(fixture.Second, "branch", "--list", "done").Trim());
    }

    /// <summary>
    /// An inspection reports every stray top-level item. Removal deletes them with the folder; they do not block.
    /// </summary>
    [Fact]
    public async Task InspectReportsStrayItemsAndRemovalDeletesThem()
    {
        Fixture fixture = Fixture.Create();
        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(new(fixture.Folder, "inspected", fixture.Repositories, false), Actor);
        Directory.CreateDirectory(Path.Combine(fixture.Folder, "Library"));
        Directory.CreateDirectory(Path.Combine(fixture.Folder, ".agent-ds"));
        File.WriteAllText(Path.Combine(fixture.Folder, "notes.txt"), "notes");

        WorkspaceInspection inspection = await fixture.Folders.InspectAsync(fixture.Folder, fixture.Repositories);
        WorkspaceRemovalResult removed = await fixture.Folders.RemoveAsync(fixture.Folder, Actor);

        Assert.All(inspection.Repositories, item => Assert.Equal(WorkspaceRepositoryState.Healthy, item.State));
        Assert.Equal([".agent-ds", "Library", "notes.txt"], inspection.Strays.Select(Path.GetFileName));
        Assert.Equal(created.Worktrees.Select(item => item.Path), removed.Worktrees);
        Assert.False(Directory.Exists(fixture.Folder));
    }

    /// <summary>
    /// A repair re-creates a deleted worktree on its branch, moves stray items into tmp/repaired-*, keeps every lock and
    /// puts uncommitted and untracked changes back.
    /// </summary>
    [Fact]
    public async Task RepairRecreatesMovesAndKeepsChanges()
    {
        Fixture fixture = Fixture.Create();
        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(new(fixture.Folder, "repair", fixture.Repositories, false), Actor);
        string kept = created.Worktrees[0].Path;
        string lost = created.Worktrees[1].Path;
        File.WriteAllText(Path.Combine(kept, "readme.txt"), "changed\n");
        File.WriteAllText(Path.Combine(kept, "untracked.txt"), "new\n");
        string status = Git.Run(kept, "status", "--porcelain");
        Directory.Delete(lost, recursive: true);
        string library = Directory.CreateDirectory(Path.Combine(fixture.Folder, "Library")).FullName;
        File.WriteAllText(Path.Combine(library, "file.txt"), "kept");

        WorkspaceRepairResult repaired = await fixture.Folders.RepairAsync(new(fixture.Folder, "repair", fixture.Repositories), Actor);

        WorkspaceRecreatedWorktree recreated = Assert.Single(repaired.Recreated);
        Assert.Equal(lost, recreated.Worktree.Path);
        Assert.Equal(GitWorktreeAttachSource.LocalBranch, recreated.Source);
        Assert.Equal("repair", Git.Run(lost, "branch", "--show-current").Trim());
        Assert.NotNull(repaired.RepairedFolder);
        Assert.StartsWith(Path.Combine(fixture.Folder, "tmp", "repaired-"), repaired.RepairedFolder, StringComparison.Ordinal);
        WorkspaceMovedItem moved = Assert.Single(repaired.Moved);
        Assert.Equal(library, moved.From);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(moved.To, "file.txt")));
        Assert.False(Directory.Exists(library));
        Assert.Single(repaired.Reapplied);
        Assert.Empty(repaired.Conflicts);
        Assert.Equal(status, Git.Run(kept, "status", "--porcelain"));
        Assert.Empty(Git.Run(kept, "stash", "list").Trim());
        Assert.True(Locked(kept));
        Assert.True(Locked(lost));
    }

    /// <summary>
    /// A worktree inside the workspace that is not a configured repository is reported apart from strays, and a repair
    /// leaves it where it is, still a working worktree.
    /// </summary>
    [Fact]
    public async Task RepairLeavesAnotherWorktreeWhereItIs()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Folders.CreateAsync(new(fixture.Folder, "main-task", fixture.Repositories, false), Actor);
        string other = Path.Combine(fixture.Folder, "second-extra");
        Git.Run(fixture.Second, "worktree", "add", "-q", "-b", "extra", other);
        string stray = Directory.CreateDirectory(Path.Combine(fixture.Folder, "Library")).FullName;

        WorkspaceInspection inspection = await fixture.Folders.InspectAsync(fixture.Folder, fixture.Repositories);
        WorkspaceRepairResult repaired = await fixture.Folders.RepairAsync(new(fixture.Folder, "main-task", fixture.Repositories), Actor);

        Assert.Equal([other], inspection.OtherWorktrees);
        Assert.Equal([stray], inspection.Strays);
        Assert.Equal([other], repaired.OtherWorktrees);
        Assert.Equal([stray], repaired.Moved.Select(item => item.From));
        Assert.Equal("extra", Git.Run(other, "branch", "--show-current").Trim());
    }

    /// <summary>A stash whose re-apply conflicts stays in the stash list, and the result names its repository, paths and commit.</summary>
    [Fact]
    public async Task RepairLeavesAConflictingStashAndReportsIt()
    {
        Fixture fixture = Fixture.Create();
        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(new(fixture.Folder, "conflict", fixture.Repositories, false), Actor);
        string worktree = created.Worktrees[0].Path;
        File.WriteAllText(Path.Combine(worktree, "readme.txt"), "uncommitted\n");
        // Between the stash and its re-apply, the branch commits another change to the same line.
        fixture.Log.OnLine = line =>
        {
            if (!line.StartsWith("Re-applying the stash", StringComparison.Ordinal)) return;
            File.WriteAllText(Path.Combine(worktree, "readme.txt"), "committed\n");
            Git.Run(worktree, "commit", "-q", "-am", "Conflicting");
        };

        WorkspaceRepairResult repaired = await fixture.Folders.RepairAsync(new(fixture.Folder, "conflict", fixture.Repositories), Actor);

        WorkspaceStashConflict conflict = Assert.Single(repaired.Conflicts);
        Assert.Equal("first", conflict.Repository.Name);
        Assert.Equal(["readme.txt"], conflict.ConflictedPaths);
        Assert.Empty(repaired.Reapplied);
        Assert.Contains(conflict.StashCommit, Git.Run(worktree, "stash", "list", "--format=%H"), StringComparison.Ordinal);
    }

    /// <summary>
    /// DSA-1088: an entry whose base names a remote-tracking branch, in a repository with no commits, is not fetched and
    /// gets a locked worktree on a new branch with no commits; the source repository still has none.
    /// </summary>
    [Fact]
    public async Task AnEntryWithARemoteBaseInARepositoryWithNoCommitsGetsAnOrphanWorktree()
    {
        Fixture fixture = Fixture.Create();
        string empty = fixture.Empty("empty");
        string emptyOrigin = Path.Combine(Path.GetDirectoryName(empty)!, "empty-origin.git");
        Git.Run(Path.GetDirectoryName(empty)!, "init", "-q", "--bare", "-b", "main", emptyOrigin);
        Git.Run(empty, "remote", "add", "origin", emptyOrigin);

        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(
            new(fixture.Folder, "remote-base", [new("empty", empty, "origin/dev")], true), Actor);

        Assert.Empty(created.Skipped);
        WorkspaceWorktree worktree = Assert.Single(created.Worktrees);
        Assert.Null(worktree.Worktree.BaseCommit);
        Assert.Equal("remote-base", Git.Run(worktree.Path, "branch", "--show-current").Trim());
        Assert.True(Locked(worktree.Path));
        Assert.Empty(Git.Run(empty, "rev-list", "--all").Trim());
    }

    /// <summary>
    /// DSA-1088: a worktree on a branch with no commits is kept while it holds an untracked file, and removed with its
    /// folder once it is clean; no branch is deleted, since none has a ref.
    /// </summary>
    [Fact]
    public async Task RemoveRemovesACleanOrphanWorktreeAndKeepsOneWithFiles()
    {
        Fixture fixture = Fixture.Create();
        string empty = fixture.Empty("empty");
        WorkspaceCreateResult created = await fixture.Folders.CreateAsync(
            new(fixture.Folder, "orphan", [new("empty", empty, "HEAD")], false), Actor);
        string worktree = Assert.Single(created.Worktrees).Path;
        string notes = Path.Combine(worktree, "notes.txt");
        File.WriteAllText(notes, "keep\n");

        await Assert.ThrowsAsync<WorkspaceRemovalBlockedException>(() => fixture.Folders.RemoveAsync(fixture.Folder, Actor));
        Assert.Equal("keep\n", File.ReadAllText(notes));
        Assert.True(Locked(worktree));

        File.Delete(notes);
        WorkspaceRemovalResult removed = await fixture.Folders.RemoveAsync(fixture.Folder, Actor);

        Assert.Equal([worktree], removed.Worktrees);
        Assert.Empty(removed.Branches);
        Assert.False(Directory.Exists(fixture.Folder));
        Assert.DoesNotContain("orphan", Git.Run(empty, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
    }

    // Whether git worktree list names the worktree locked with the workspace reason. Git prints '/' on every system.
    private static bool Locked(string worktree)
    {
        string suffix = "/" + Path.GetFileName(Path.GetDirectoryName(worktree)) + "/" + Path.GetFileName(worktree);
        return Git.Run(worktree, "worktree", "list", "--porcelain").Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n")
            .Any(record => record.Split('\n')[0].Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal)
                && record.Contains("\nlocked " + WorkspaceFolders.LockReason, StringComparison.Ordinal));
    }

    /// <summary>Two repositories, the first with an origin holding dev, and the workspace operations over them.</summary>
    private sealed class Fixture
    {
        private Fixture(string root)
        {
            this.Origin = Path.Combine(root, "origin.git");
            Git.Run(root, "init", "-q", "--bare", "-b", "main", this.Origin);
            this.First = Init(Path.Combine(root, "first"));
            Git.Run(this.First, "remote", "add", "origin", this.Origin);
            Git.Run(this.First, "push", "-q", "origin", "main:dev");
            Git.Run(this.First, "fetch", "-q", "origin");
            this.Second = Init(Path.Combine(root, "second"));
            // The second repository's base must be on a remote-tracking branch for a removal to lose nothing.
            string secondOrigin = Path.Combine(root, "second-origin.git");
            Git.Run(root, "init", "-q", "--bare", "-b", "main", secondOrigin);
            Git.Run(this.Second, "remote", "add", "origin", secondOrigin);
            Git.Run(this.Second, "push", "-q", "-u", "origin", "main");
            this.Folder = Path.Combine(root, "workspaces", "work");
            this.Repositories = [new("first", this.First, "origin/dev"), new("second", this.Second, "main")];
            GitClient git = new(new SystemGitProcessLauncher(), UnrestrictedGitPathPolicy.Instance, new GitRepositoryLocks());
            this.Folders = new(git, UnrestrictedGitPathPolicy.Instance, this.Log);
        }

        public string Origin { get; }
        public string First { get; }
        public string Second { get; }
        public string Folder { get; }
        public IReadOnlyList<WorkspaceRepository> Repositories { get; }
        public RecordingLogger Log { get; } = new();
        public WorkspaceFolders Folders { get; }

        public static Fixture Create() => new(TestScratchRoot.CreateDirectory("workspaces-"));

        // A repository with no commits beside the fixture's repositories.
        public string Empty(string name) => InitEmpty(Path.Combine(Path.GetDirectoryName(this.First)!, name));

        // Commits a file in a clone of origin and pushes it to dev, so only a fetch brings it; returns the commit.
        public string PublishToOrigin(string repository, string file)
        {
            string clone = Path.Combine(Path.GetDirectoryName(repository)!, "publisher");
            Git.Run(Path.GetDirectoryName(repository)!, "clone", "-q", "-b", "dev", this.Origin, clone);
            File.WriteAllText(Path.Combine(clone, file), file);
            Git.Run(clone, "add", file);
            Git.Run(clone, "commit", "-q", "-m", file);
            Git.Run(clone, "push", "-q", "origin", "dev");
            return Git.Run(clone, "rev-parse", "HEAD").Trim();
        }

        private static string InitEmpty(string directory)
        {
            Directory.CreateDirectory(directory);
            Git.Run(directory, "init", "-q", "-b", "main");
            // The library's own stash commits use the repository's identity.
            Git.Run(directory, "config", "user.name", "Workspace Test");
            Git.Run(directory, "config", "user.email", "workspace@example.invalid");
            Git.Run(directory, "config", "commit.gpgsign", "false");
            Git.Run(directory, "config", "core.autocrlf", "false");
            return directory;
        }

        private static string Init(string directory)
        {
            InitEmpty(directory);
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "readme\n");
            Git.Run(directory, "add", "readme.txt");
            Git.Run(directory, "commit", "-q", "-m", "Base");
            return directory;
        }
    }

    /// <summary>Keeps each formatted log line and calls <see cref="OnLine"/> with it as it is written.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> lines = [];

        public Action<string>? OnLine { get; set; }

        public IReadOnlyList<string> Lines
        {
            get { lock (this.lines) return [.. this.lines]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception);
            lock (this.lines) this.lines.Add(line);
            this.OnLine?.Invoke(line);
        }
    }

    /// <summary>Runs Git for test setup and checks, failing the test on a non-zero exit.</summary>
    private static class Git
    {
        public static string Run(string directory, params string[] arguments)
        {
            ProcessStartInfo start = new("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string argument in (string[])["-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false", .. arguments])
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {errors.Result}");
            return output.Result;
        }
    }
}
