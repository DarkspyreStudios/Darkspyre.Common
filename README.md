# Darkspyre.Common

Shared libraries for Darkspyre applications. Each library is its own project and DLL under `src/`, with its tests under
`tests/`. This file is the design record: one section per library.

Consumers build these libraries from source. A consuming repository sets `DarkspyreCommonSourceRoot` to a
Darkspyre.Common checkout and imports `eng/SourceReferences.targets` from its `Directory.Build.targets`; each
`Darkspyre.Common.*` `PackageReference` then becomes a `ProjectReference` with its asset metadata kept. No package is
published.

## Darkspyre.Common.Git

Git operations over the installed `git` executable. The library supports Git 2.34 or newer and uses no Git feature newer
than 2.34: each Git action has one implementation, with no branching on the Git version.

### Starting it

```csharp
GitCapabilities git = await GitVersion.ReadAsync(new SystemGitProcessLauncher(), workingDirectory, cancellationToken);
if (!git.IsAvailable) throw new NotSupportedException(git.Diagnostic);
GitRepositoryLocks locks = new(); // one per process
GitClient client = new(new SystemGitProcessLauncher(), UnrestrictedGitPathPolicy.Instance, locks);
GitRepositorySnapshot? snapshot = await client.InspectAsync(directory, cancellationToken);
```

`GitClient` checks no version. A consumer reads `GitVersion` once before its first call; an older Git fails per command.
Its optional `readPolicy` argument defaults to `GitReadPolicy.Configured`. Search passes `GitReadPolicy.LocalOnly`:
all read commands, including the persistent blob process, receive an empty `GIT_ALLOW_PROTOCOL` transport allowlist.
They cannot fetch from network, file or helper transports, even when repository configuration allows them.
This process-local policy changes no Git configuration and does not change explicit mutation operations.
The setting is supported by [Git 2.34](https://git-scm.com/docs/git/2.34.0#Documentation/git.txt-codeGITALLOWPROTOCOLcode).

### Seams

- `IGitProcessLauncher` starts every Git child. `GitProcessStart` carries the arguments, working directory, environment
  entries (a null value removes the entry) and `GitProcessKind`. A `Read` child's output can name files the caller may
  not see, so a launcher keeps no copy of it; a `Change` child's output may be kept. `SystemGitProcessLauncher` starts
  `git` from PATH with `System.Diagnostics.Process`. Its stop request and forced stop both kill the child only; a
  descendant exits when its pipes to the child close.
- `IGitPathPolicy` is the caller's view of the file system. The library asks it before any Git child starts for a
  caller's directory (`GetReadAccessAsync`, then it locates the working tree in `Resolve(directory)`), before every
  change (`RequireWriteAsync`), and for each path a read returns; a path it refuses is left out of the result.
  `GetCloneWorkingDirectoryAsync` names the existing directory `git clone` runs in. `UnrestrictedGitPathPolicy`
  permits every path, resolves links in every component with `FileSystemInfo.ResolveLinkTarget`, does not fold letter
  case, and runs a clone in the nearest existing ancestor of the destination.
- `GitRepositoryLocks` serializes changes per Git common directory. The key is the path as given, so every caller passes
  a path resolved through the path policy. One instance is shared by every client of a process.

### Behaviour

- Every Git run captures through one internal path, `GitCapture`: it writes the input and closes standard input, drains
  both output channels together, applies an optional output bound across both channels (output past the bound is read
  and dropped), and on a timeout requests a stop, then forces it after 5 seconds.
- Reads run with `--no-replace-objects --no-optional-locks`, `GIT_NO_REPLACE_OBJECTS=1`, and every inherited trace,
  namespace and `GIT_CONFIG_*` variable cleared.
- Object existence replaces lazy-fetch suppression. A blob reader reads once whether the repository is a partial clone
  (`extensions.partialclone`, or a `remote.<name>.promisor` that Git reads as true). Only then does each blob read first
  run `git rev-list --objects --no-object-names --no-walk --ignore-missing --missing=print`. A missing object throws
  `GitReadException` with `GitReadFailure.ObjectMissing` and nothing is fetched. Other reads (tree listings, diffs,
  history, patch IDs) run no existence check and can fetch in a partial clone under `Configured`, as plain Git does.
  `LocalOnly` prevents those transports; failed reads and content-read diagnostics report unavailable local content,
  never a complete empty tree. Existing object-existence checks still apply to blobs.
- A file read lists the entry with `ls-tree` without `-l`; the byte limit is checked from the `cat-file --batch` header
  before the body is read.
- A commit runs `git add` on the named paths present in the worktree, so new files are known to Git, then
  `commit --only` on every named path. A named path absent from the worktree is committed as its deletion, also when
  that deletion is already staged. Other staged entries stay staged.
- A fetch reads the commit of each `source:destination` destination with `for-each-ref` before and after
  `fetch --atomic`. `GitFetchResult.UpdatedRefs` lists each destination whose commit changed, in request order. A failed
  fetch returns its diagnostic.
- A rebase runs `git -c rebase.updateRefs=false rebase`, so it moves no other branch.
- A cherry-pick runs a plain pick. When a pick stops with `CHERRY_PICK_HEAD` present, no unmerged path and an index equal
  to HEAD, the commit was already applied: the library runs `cherry-pick --skip`, at most once per source commit. A stop
  with staged changes, such as one a hook caused, stays stopped. Skipped commits are named by stable patch ID.
- `InspectAsync` identifies both the common directory and the individual worktree administrative directory.
  A caller can watch that worktree's HEAD/index without inferring an administrative name from its absolute checkout path.
- `ReadWorktreesAsync` lists registered worktrees, their observed HEAD and branch, including unavailable locations.
  Paths the caller's policy refuses are omitted. `ReadLocalBranchesAsync` lists local branch refs and exact commit IDs.
  Neither read contacts a remote or changes Git. A failed read throws and never represents an empty inventory.
  Inventory diagnostics also fail the read: Git can omit broken refs with a warning while returning exit zero.
- Every caller of `git worktree list --porcelain` uses one parser. Incomplete or unsupported output throws, including
  newline-containing paths that the line protocol cannot represent. Callers cannot infer deletion from that failure.
- `LockWorktreeAsync` locks a linked worktree with a reason and leaves an existing lock as it is; `UnlockWorktreeAsync`
  unlocks one that is locked. `UnlockWorktreeRecordAsync` unlocks the record of a worktree whose folder may be gone:
  `git worktree prune` keeps a locked record, and its branch stays checked out there. Git refuses to remove a locked worktree, so a caller that removes its own locked worktree
  unlocks it first. `PruneWorktreesAsync` runs `git worktree prune`. `AttachWorktreeAsync` adds a worktree on a branch
  that may exist: the local branch, else a new untracked branch at `origin/<branch>`, else a new untracked branch at the
  base; the result says which ([DSA-1028](https://darkspyre.atlassian.net/browse/DSA-1028)).
- A repository-relative path has no empty, `.` or `..` component.
- `GitPathChecks.NamedPathExists` reports whether a directory entry exists at a path without following a final link, so
  a new clone or worktree never takes the place of a dangling link.

### Tests

`tests/Darkspyre.Common.Git.Tests` runs against the real `git` on PATH. Every folder the tests create lives in a
per-process scratch root, `<parent>/darkspyre-common-tests-<process id>`. The parent is `AGENTDS_TEST_SCRATCH` when set,
else `/Volumes/Data/tmp` when it exists; with neither, the tests fail at start. `GitVersionTests` writes the Git
version a run used to the test output.

## Darkspyre.Common.Pdf and Darkspyre.Common.Pdf.Pdfium

A PDF engine in two DLLs. It opens documents and inspects their pages, renders pages, extracts text with positions, and
reads bookmarks and internal links.

- `Darkspyre.Common.Pdf` is the portable contract every consumer compiles against: `IPdfEngine`, `IPdfDocument`, their
  result records, and `UnavailablePdfEngine`, which a build with no PDF implementation returns. Every coordinate is in
  points from the top-left of the page as displayed, after the page's rotation. Pixels are 8-bit RGBA, top row first,
  with no row padding.
- `Darkspyre.Common.Pdf.Pdfium` is the PDFium implementation, `PdfiumEngine`, for 64-bit macOS and Linux
  (`[SupportedOSPlatform("macos")]`, `[SupportedOSPlatform("linux")]`). It holds the native bindings, so it is a
  platform implementation under the operating-system boundary rule.

### Starting it

```csharp
IPdfEngine engine = new PdfiumEngine(); // on a platform without PDFium: new UnavailablePdfEngine()
PdfOpenResult open = await engine.OpenAsync(bytes, password: null, cancellationToken);
if (open.Status != PdfOpenStatus.Opened) { /* ask for a password, then open again with it */ }
await using IPdfDocument document = open.Document!;
PdfBitmap? tile = await document.RenderAsync(0, 2, new PdfPixelArea(0, 0, 512, 512), cancellationToken);
```

`Darkspyre.Common.Pdf.Pdfium` references no native package. The consumer's platform build links its runtime's PDFium
native package (`bblanchon.PDFium.macOS` or `bblanchon.PDFium.Linux`), and `LibraryImport("pdfium")` resolves in the
load context of `Darkspyre.Common.Pdf.Pdfium.dll`. No portable code checks the operating system.

### Behaviour

- PDFium is not thread-safe. One lock per load context serializes every PDFium call, and the library is initialized on
  first use and never destroyed. A consumer shares one engine instance, so every caller in an application goes through
  one lock.
- An open copies the bytes into native memory that lives until the document is disposed. A file that needs a password
  reports `PasswordRequired` when none is given and `IncorrectPassword` when the given one fails.
- A render draws an area of a page with its annotations and form fields. The area is at most 1024 pixels a side; a
  larger page is drawn as tiles.
- Text comes as lines in reading order, each with its box. A valid blank page returns an empty list. An invalid page index
  throws `ArgumentOutOfRangeException`; failed native page/text reads throw `IOException` with
  `PDF page text could not be read.`. Search can distinguish unreadable pages from pages that need OCR.
  Links list only targets inside the document.

### Tests

`tests/Darkspyre.Common.Pdf.Tests` runs the real PDFium engine against the fixtures `three-pages.pdf` and
`three-pages-protected.pdf` (password `secret`). The test project references both PDFium native packages, and the
runtime picks its own native file. The test class carries both `SupportedOSPlatform` attributes.

## Avalonia viewers

Four libraries hold viewers another app can use: `Darkspyre.Common.Avalonia.Markdown`, `.Code`, `.Pdf` and `.Terminal`.
Each is a `TemplatedControl` with its own API; the third-party library behind it is an implementation detail, so
replacing one changes one project and no caller. None references agent-ds. Each viewer carries its own styles, so a
host adds nothing to its application. Colours and fonts come from these resource keys when the host supplies them, and
fall back to light or dark defaults when it does not: `panel`, `text`, `accent` and `line` as `Color`; `x.brush` and
the translucent `x.brush.NN` variants as brushes; the text entry fonts `font-body`, `font-code` and `font-heading-1` to
`font-heading-6` as `FontFamily`; and their sizes `font-size-body`, `font-size-code` and `font-size-heading-1` to
`font-size-heading-6` as `double` layout units. agent-ds supplies all of them from the theme.

`MarkdownViewer` draws every link from two more keys: `link` as a `Color`, and `link-underline` as a `bool`. A link
takes the `link` colour, else the `accent` colour, and is underlined only when `link-underline` is `true`; without the
key, or with `false`, a link is not underlined. The key is a `bool` because a host may build its resources on a thread
other than the UI thread, and the viewer makes the underline on the UI thread. The viewer follows both keys when they
change.

`MarkdownViewer` sets every text block's font from these keys after LiveMarkdown's own styles, which otherwise fix
Arial at 14 (headings 24 to 16) whatever the parent's font: paragraphs, quotes, list items, table cells and list
markers take `font-body` at `font-size-body`, a heading of level N takes `font-heading-N` at `font-size-heading-N`,
and inline code and code blocks take `font-code` at `font-size-code`. LiveMarkdown classes heading text
`Heading1` to `Heading6`, the other text `ParagraphBlock`, and list markers `ListBlockBullet` and `ListBlockNumber`.

- **`MarkdownViewer`** renders Markdown through LiveMarkdown. Raw HTML is shown as text; generic attributes and media
  embeds are off. A link or image target that is not an absolute http or https address with a host, a mailto address
  or a relative path is emptied, so it cannot be followed or loaded. Activating an allowed link raises `LinkActivated`;
  the viewer opens nothing itself. A caller that sets `LinkSource` (an `IMarkdownLinkSource`) gets its own parse
  pipeline: an http, https or mailto target is still checked as above, every other target is a link only when the
  source's `Accepts` is true, and a refused link shows as its own text, not as an inert link. The source also adds
  links: each span its `Find` returns in plain text outside links, with adjacent literals searched as one text, and
  each code span whose whole trimmed text it accepts. `Find` and `Accepts` may run off the UI thread; a click on a link
  that is not a web or mail address activates only when `Accepts` is true for its target. Setting the source, or the
  source raising `Changed`, parses the whole text again. Without a source, links follow the allow-list alone.
  Selection follows LiveMarkdown's renderer, which keeps a drag inside one viewer. `SelectAll` selects every block, and
  `SelectedText` is then the Markdown source (`Text`) for as long as that selection lasts; any other selection gives the
  rendered text. `CopyAsync` writes `SelectedText` to the window's clipboard and returns false when nothing is
  selected, there is no clipboard or the write fails; it shows no toast. The context menu offers Copy (enabled only
  while `CanCopy`) and Select All, and the platform's Copy and Select All keys run the same two actions before
  LiveMarkdown's own key handling. Code blocks have a copy button and no syntax colouring; the viewer does the button's
  copy itself, and once the code is on the clipboard the button shows a check and is named Copied for two seconds, then
  the copy glyph and the name Copy return. A second press restarts the two seconds. While `Streaming` is true
  and the new text extends the old, only the tail is appended. A `mermaid` fence becomes a diagram drawn by
  `IMermaidRenderer` (default `MermaiderRenderer`: Mermaider, then `SvgCssFlattener`, which resolves the CSS custom
  properties and colour mixes Svg.Skia cannot draw). A fence the renderer cannot draw stays a code block. Diagrams are
  drawn again when the theme changes their colours or font, and not for a resource change that leaves them as they
  were. The colour tokens are read whole on each resource or theme change and handed to LiveMarkdown as one resource
  dictionary, only when a colour differs, so a change restyles the rendered text once and an unrelated one not at all
  ([DSA-1102](https://darkspyre.atlassian.net/browse/DSA-1102)).
- **`SourceView`** shows read-only text in AvaloniaEdit with line numbers and TextMate highlighting chosen by file
  extension or language id. `Find` marks every case-insensitive match; `FindNext` and `FindPrevious` wrap. `GoToLine`
  scrolls to a line; asked for before the text is set, it applies when the text arrives. A new `Text` keeps the
  scroll position. The highlighter exists only while the view is in a window.
- **`DiffView`** compares `OldText` and `NewText` with `TextDiff` off the UI thread and shows a unified diff: a header
  per hunk, old and new line numbers, plus and minus marks, added and removed rows shaded, and a "No final newline"
  note. Line endings take part in the comparison. Equal texts show "No text differences."; a comparison over the work
  limit (4 million table cells) shows its error. `Find` works on the shown diff as it does in `SourceView`. `TextDiff`
  and its line types are public for callers that compare without a view.
- **`PdfViewer`** shows an `IPdfDocument` the caller opened (password included) and disposes after the viewer. It has a
  toolbar (side panel, zoom out, zoom list, zoom in, page status) and a side panel with thumbnails and bookmarks.
  Pages are drawn in tiles of up to 1024 device pixels, two at a time, only near the visible area. A zoom change
  redraws the tiles; a resize that leaves the scale as it was keeps them. Find highlights are redrawn only when the
  matches, the shown match or a page's text layer change. Each page's text is
  read once in the background into transparent, selectable lines over the page; internal links scroll to their
  target. `Find`, `FindNext` and `FindPrevious` search the text read so far; `CurrentMatch` is the shown match. `Zoom` is a percentage from 50 to 400,
  fit width or fit page.
- **`TerminalView`** is a VT screen over RoyalTerminal with no process of its own. `Write` feeds the process's output
  in; the person's keystrokes come out of `InputReceived` already encoded, and `ScreenSizeChanged` reports the cell
  size when the view is resized. Both run through a transport the view owns, so RoyalTerminal starts no shell. The
  screen's background and text colours follow `panel` and `text`, and a resource pass that leaves a colour as it was
  sets nothing. The owner disposes the view when its panel closes.

## Darkspyre.Common.Workspaces

Workspace folders: a folder holding one git worktree per repository, all on one branch, each locked, and the folder
`tmp` with `tmp/home`. `WorkspaceFolders` is the one implementation that creates, inspects, removes and repairs them.
It runs every Git operation through `Darkspyre.Common.Git` and holds no state; it knows nothing of sessions or of who
uses a workspace ([DSA-1028](https://darkspyre.atlassian.net/browse/DSA-1028)).

### Starting it

```csharp
GitClient git = new(launcher, paths, locks);
WorkspaceFolders folders = new(git, paths, logger);
WorkspaceCreateResult created = await folders.CreateAsync(
    new(folder, "dsa-1/work", [new("agent-ds", agentDsRepository, "origin/dev")], SkipFailures: false),
    new WorkspaceActor($"session {sessionId}", "new session"), cancellationToken);
```

The path policy must be the Git client's own, so the library compares paths the way Git reports them. Every call takes
a `WorkspaceActor`, the acting owner and the trigger, which each log line names.

### Behaviour

- Every step writes one Information line naming its path, the actor and the trigger: creating the folder, fetching a
  base, creating, locking, unlocking and removing a worktree, deleting a branch or the folder, stashing, pruning,
  re-creating, moving an item and re-applying a stash.
- Every worktree the library makes is locked with `WorkspaceFolders.LockReason`, "Darkspyre session workspace; do not
  remove", so Git and tools that clean up worktrees leave it alone. The library unlocks a worktree just before it
  removes it, and locks it again when Git keeps it.
- `CreateAsync` refuses an existing folder. For each repository in order it fetches a base that names a
  remote-tracking branch, creates the worktree at `<folder>/<name>` on the new branch and locks it; then it creates
  `tmp/home`. With `SkipFailures` a repository whose fetch or worktree fails is skipped and reported with its reason;
  otherwise, and on cancellation, the first failure undoes everything and is thrown. A creation that makes no
  worktree deletes the folder. It changes nothing outside the folder and the repositories. A repository with no
  commits is not fetched, and its worktree's new branch starts with no commits (`git worktree add --orphan`), whatever
  its base names; the repository itself still has no commits
  ([DSA-1088](https://darkspyre.atlassian.net/browse/DSA-1088)). The same rule holds for every worktree
  `Darkspyre.Common.Git` creates: a prepared worktree in a repository with no commits has a null `BaseCommit`, and its
  branch starts with none even when the repository has gained a commit by the time it is created.
- `InspectAsync` reports each configured repository as healthy (a worktree's own folder with the repository's common
  directory, locked or not), missing or broken; every other top-level folder that is a git worktree's own folder, such
  as one made for a single task, as another worktree; and every remaining top-level item except `tmp` as a stray, so
  a caller can warn instead of refusing.
- `RemoveAsync` takes a workspace folder or a single worktree. Every worktree must lose nothing but ignored files and
  have every commit on a remote-tracking branch, and a workspace must hold nothing but worktrees and `tmp`; otherwise
  `WorkspaceRemovalBlockedException` names the cause and nothing is removed. It unlocks and removes each worktree,
  deletes each branch while it still names the removed head, and deletes the folder. A worktree on a branch with no
  commits is removable when it holds no tracked, untracked or staged files; its branch has no ref, so none is deleted.
- `RepairAsync` stashes each healthy worktree's uncommitted and untracked changes (`darkspyre-repair <timestamp>`);
  moves each broken repository folder into `tmp/repaired-<yyyyMMdd-HHmmss>`; unlocks each missing worktree's record and
  prunes the repositories; re-creates each missing worktree on the branch (the local branch, else a new branch at
  `origin/<branch>`, else a new branch at the base, fetched first); locks every configured worktree, keeping existing
  locks; moves every stray item, folder or file, into the repair folder, and leaves another worktree where it is and
  reports it; then re-applies each stash by its commit and
  drops it only when it applied cleanly. A conflicting stash stays, the worktree keeps its conflicts, and the result
  names the repository, the paths and the stash commit. Nothing is deleted.

### Notes for callers

- The library does not track which workspaces an owner created. When a session is deleted, the caller removes every
  workspace that session created, so it records them as it creates them (Brandon, 2026-10-14).

### Tests

`tests/Darkspyre.Common.Workspaces.Tests` runs against the real `git` on PATH, in the same per-process scratch root as
the Git tests. Each test builds two repositories with bare origins.

## Darkspyre.LocalServices.Contracts

The independent contract library contains service installation, ownership and subscription interfaces,
wire messages and the shared launch/package/result types. It references .NET only. Consumers can use
`IServiceConnector`, `IServiceSubscription` and `IServicePlatform` without referencing the implementation,
Connect, Search or Harness. Existing types retain their namespaces.

## Darkspyre.LocalServices

The runtime implements the connector, subscription, request transport, launch budget and service drain.
It references Contracts. A subscriber library installs its verified package through `IServicePlatform`,
constructs `ServiceConnector`, and owns the returned `IServiceSubscription`. The first connection launches
the service; subsequent connections join it. The final departure starts the existing drain.
The application Host does not register, install or start the service.
The package cache copies a payload into a staging folder and syncs each file with `ServiceFileSystem.SyncFile`. It then
flushes the device once with `ServiceFileSystem.FlushDevice` and only after that publishes the build by rename. On macOS
the one flush is F_FULLFSYNC; on Linux and Windows the per-file sync already flushes the device.

## Darkspyre.LocalServices.Platform.MacOS, Linux and Windows

Each platform library implements `IServicePlatform` and owns native files, locks, transport and detached
launch. It references Contracts and Runtime. `build/LocalServices/ServicePlatform.props` selects the
implementation from the target RID. The runtime and contracts contain no platform selection.
The libraries build from Common without Harness build imports. The disposable fixture executable lives
in `tests/Darkspyre.LocalServices.TestBackend`; AgentDSPlugins retains its existing lifecycle tests.
The Linux implementation supports glibc on x64 and ARM64. It selects the architecture's file-status
layout and directory/no-follow open flags inside the Linux library. Consumers use the same contracts.

[Local service contracts and lifecycle](docs/local-services.md) describes the wire, installation and
ownership behavior. The [shared service design](https://darkspyre.atlassian.net/browse/DSA-855) records this boundary.

## LocalServices packaging tool

`build/Darkspyre.LocalServices.Packaging` supplies the shared `ServicePackageBuild` API and a
`finalize <directory>` CLI. It captures actual service files through the existing platform file seam.
Its portable DLL selects the running OS inside its composition and needs no Harness or Search dependency.
The tool runs during builds/signing and is excluded from subscriber payloads.

Mac signers sign inner files first, then refresh each `service/manifest.json` from its sibling
`service/payload/`. The helper writes changed canonical inventories only and returns their paths.
The signer signs those manifests before sealing/verifying the containing package. Unchanged retained
copies keep their bytes and signatures. Runtime installation verifies the final manifest and every
payload file through the existing immutable cache. This ordering follows
[DSA-855](https://darkspyre.atlassian.net/browse/DSA-855), Tech Design section 5.12.4a.
