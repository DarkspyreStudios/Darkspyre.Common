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
  history, patch IDs) run no existence check and can fetch in a partial clone, as plain Git does.
- A file read lists the entry with `ls-tree` without `-l`; the byte limit is checked from the `cat-file --batch` header
  before the body is read.
- A fetch reads the commit of each `source:destination` destination with `for-each-ref` before and after
  `fetch --atomic`. `GitFetchResult.UpdatedRefs` lists each destination whose commit changed, in request order. A failed
  fetch returns its diagnostic.
- A rebase runs `git -c rebase.updateRefs=false rebase`, so it moves no other branch.
- A cherry-pick runs a plain pick. When a pick stops with `CHERRY_PICK_HEAD` present, no unmerged path and an index equal
  to HEAD, the commit was already applied: the library runs `cherry-pick --skip`, at most once per source commit. A stop
  with staged changes, such as one a hook caused, stays stopped. Skipped commits are named by stable patch ID.
- `ReadWorktreesAsync` lists registered worktrees, their observed HEAD and branch, including unavailable locations.
  Paths the caller's policy refuses are omitted. `ReadLocalBranchesAsync` lists local branch refs and exact commit IDs.
  Neither read contacts a remote or changes Git. A failed read throws and never represents an empty inventory.
  Inventory diagnostics also fail the read: Git can omit broken refs with a warning while returning exit zero.
- Every caller of `git worktree list --porcelain` uses one parser. Incomplete or unsupported output throws, including
  newline-containing paths that the line protocol cannot represent. Callers cannot infer deletion from that failure.
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
