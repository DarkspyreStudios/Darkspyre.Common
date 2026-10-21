# Darkspyre.Common

Shared libraries for Darkspyre applications. Each library is its own project and DLL under `src/`, with its tests under
`tests/`. This file is the design record: one section per library.

Consumers build these libraries from source. A consuming repository sets `DarkspyreCommonSourceRoot` to a
Darkspyre.Common checkout and imports `eng/SourceReferences.targets` from its `Directory.Build.targets`; each
`Darkspyre.Common.*`, `Darkspyre.Agent.Common.UI` and `Darkspyre.Mobile.*` `PackageReference` then becomes a
`ProjectReference` with its asset metadata kept. No package is published.

`Darkspyre.Common.slnx` builds every library except `Darkspyre.Mobile.Maui`, on macOS, Linux and Windows, from this
repository alone. `Darkspyre.Mobile.slnx` builds `Darkspyre.Mobile.Maui`; it needs the .NET MAUI iOS workload and the
sibling `oauth-broker` and `SecureStore` worktrees, which only that project imports. Missing source fails its build.

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

### Command lines

`GitCommandLine.RunAsync` runs `GitCommandLineRequest.Arguments` exactly as given, with no added option, path check,
repository lock or version check. The caller confines the working directory and every folder in the arguments.
It starts a `Change` child through the supplied launcher and returns `GitCommandLineResult` with the exit, both output
channels, timeout state and every byte read, including bytes dropped at the output bound.

Its environment derives from the library's one environment list. It clears ambient repository overrides and
`GIT_CONFIG_PARAMETERS`, inherits `GIT_OPTIONAL_LOCKS` and `LC_ALL`, clears `GIT_EDITOR` and `GIT_SEQUENCE_EDITOR`,
and sets `GIT_TERMINAL_PROMPT=0`. Environment config supplies `core.editor=false`; a caller's `-c core.editor=...`
still wins. Standard input is closed. A command that needs an editor or credential prompt fails without waiting.
The command-line and client entries share `GitCommands.StartAndCaptureAsync` for start, disposal and capture.

There are two kinds of entry into that one capture path:

| Entry | Caller | Adds | Environment | Bound and timeout |
| --- | --- | --- | --- | --- |
| `GitCommands.RunAsync`, `RunReadAsync`, `RunSequenceAsync` | Every `GitClient` operation | `--no-pager -c core.fsmonitor=false`, and the read options for reads | The library environment, or its read or sequence variant | As each operation sets them; the configured commands set neither |
| `GitCommands.RunCommandLineAsync` | `GitCommandLine.RunAsync` only | Nothing | The command-line environment | The caller's |

The `GitClient` operations check paths through the path policy, take the repository gate and classify failures into
`GitFailure` values with library-written messages; the command-line entry does none of these.

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
  and dropped), reports every byte read across both channels, and on a timeout requests a stop, then forces it after
  5 seconds.
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
- `CommitStagedAsync` commits the index as it is with `commit --no-edit -m`; it runs no `add` and no `--only`, so a
  working edit made after staging stays pending. A detached HEAD throws `StaleState`; a refused commit throws
  `CommandFailed`. `UnstageAsync` runs `restore --staged --source=HEAD` on the named paths, or, on a branch with no
  commits, `update-index --force-remove`, which keeps the working file even when its bytes differ from the staged ones.
- `ReadFileAtIndexAsync` lists the path with `ls-files --stage -z` and reads its stage-zero blob through the blob reader,
  with the same header byte check as a commit read. A path with only conflict stages throws `NotClean`; a path the
  index does not hold returns `Present` false.
- `ReadBranchesAsync` lists `refs/heads/` and `refs/remotes/origin/` with one `for-each-ref`: each branch's commit, a
  local branch's upstream and the worktree it is checked out in. Symbolic refs such as `origin/HEAD` are left out, and
  a worktree the path policy refuses is reported as null.
- `CheckoutAsync` takes a full ref from that list and never forces, resets or cleans. A local branch runs
  `switch --no-guess`. An origin branch switches to the local branch of the same name when it tracks that ref, refuses
  with `BranchExists` when it tracks another, and otherwise runs `switch --create <name> --track <ref>`. An operation in
  progress throws `StaleState`; Git's "would be overwritten by checkout" refusal throws `NotClean`.
- `ReadFileHistoryAsync` resolves the revision once and runs `log --follow --find-renames --name-status -z` for
  `Skip + Limit + 1` commits from the start, because `--follow` cannot skip and still follow renames. Each version names
  the file's path in that commit; the history ends before the first path the path policy refuses.
- `FetchConfiguredAsync`, `PullConfiguredAsync` (`pull --no-edit`), `PushConfiguredAsync` (`push --porcelain`) and
  `ResolveConflictAsync` (`mergetool --gui --no-prompt`) live in the internal `GitConfiguredCommands`. They run under
  the repository gate through `GitCommands.RunAsync` with no timeout, so Git's remotes, refspecs, upstreams, pull policy,
  credential helpers and merge tool configuration apply. Each returns a `GitCommandResult`: `Completed` for exit 0;
  `Failed` with a `GitFailure` for another exit; `Unknown` after a cancellation or a lost process, never retried. A
  porcelain `!` line is `RemoteRejected`; Git's fixed English text classifies `AuthenticationFailed` ("Authentication
  failed", "could not read Username"), `NoUpstream` ("has no upstream branch", "There is no tracking information") and
  a merge tool that cannot start ("is not available as"); anything else is `CommandFailed` with the exit code. Resolve
  first requires the path to be unmerged and `merge.guitool` or `merge.tool` to be set, and otherwise returns
  `MergeToolUnavailable` without starting anything. The diagnostic is the library's sentence, never Git's output.
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
- `RemoveWorktreeAsync` removes a linked worktree only when nothing would be lost and never forces.
  `ForceRemoveWorktreeAsync` removes an unlocked linked worktree whatever it holds, with `git worktree remove --force`;
  when Git fails or leaves the folder, the library deletes the folder and runs `git worktree prune`. It refuses the
  primary checkout. `RemoveOwnedWorktreeAsync` forces the same way while the worktree still holds the repository, path
  and branch a core module recorded. None of the three deletes the branch
  ([DSA-1203](https://darkspyre.atlassian.net/browse/DSA-1203)).
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
- `Darkspyre.Common.Pdf.Pdfium` is the PDFium implementation, `PdfiumEngine`, for 64-bit macOS, Linux and Windows
  (`[SupportedOSPlatform("macos")]`, `[SupportedOSPlatform("linux")]`, `[SupportedOSPlatform("windows")]`). It holds
  the native bindings, so it is a platform implementation under the operating-system boundary rule. The bindings use
  `CULong` for C `unsigned long`, which is 64 bits on macOS and Linux and 32 bits on Windows, and every other type in
  them has one size on all three.

### Starting it

```csharp
IPdfEngine engine = new PdfiumEngine(); // on a platform without PDFium: new UnavailablePdfEngine()
PdfOpenResult open = await engine.OpenAsync(bytes, password: null, cancellationToken);
if (open.Status != PdfOpenStatus.Opened) { /* ask for a password, then open again with it */ }
await using IPdfDocument document = open.Document!;
PdfBitmap? tile = await document.RenderAsync(0, 2, new PdfPixelArea(0, 0, 512, 512), cancellationToken);
```

`Darkspyre.Common.Pdf.Pdfium` references no native package. The consumer's platform build links its runtime's PDFium
native package (`bblanchon.PDFium.macOS`, `bblanchon.PDFium.Linux` or `bblanchon.PDFium.Win32`), and
`LibraryImport("pdfium")` resolves `libpdfium.dylib`, `libpdfium.so` or `pdfium.dll` in the
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
`three-pages-protected.pdf` (password `secret`). The test project references the three PDFium native packages, and the
runtime picks its own native file. The test class carries the three `SupportedOSPlatform` attributes.

## Darkspyre.Common.Markdown

The one Markdown safety policy every Darkspyre surface parses with. It references only Markdig and no UI framework. The
desktop's `MarkdownViewer` and the mobile `MarkdownView` both build their pipelines from it.

### Starting it

```csharp
MarkdownPipelineBuilder builder = MarkdownSafety.CreateBuilder(linkSource); // null: the allow-list alone
builder.Extensions.Add(new MyRendererExtension());                         // the caller's own extensions
MarkdownPipeline pipeline = MarkdownSafety.Build(builder);                  // turns HTML off and builds
```

### Behaviour

- `CreateBuilder` takes Markdig's advanced preset without `GenericAttributesExtension` and `MediaLinkExtension`, so
  Markdown cannot add attributes such as `{onclick=x}` or embed players, and adds the link check. `Build` applies
  `DisableHtml`, so raw HTML is parsed as text, and builds the pipeline.
- `MarkdownSafety.IsAllowedTarget` allows an absolute http or https address with a host, a mailto address, and a
  relative path. It refuses an empty target, one with surrounding white space, a control character, `"`, `<`, `>` or
  `\`, and one starting `//`.
- Without a link source, a refused link or image target is emptied, so it cannot be followed or loaded, and a refused
  autolink such as `<javascript:alert(1)>` keeps no address.
- With an `IMarkdownLinkSource`, an http, https or mailto target is still checked by `IsAllowedTarget`, and every other
  target is a link only when the source's `Accepts` is true. A refused link shows as its own text, a refused image is
  emptied and a refused autolink becomes literal text. `MarkdownLinkSpan` is the span a source's `Find` returns; the
  desktop viewer uses `Find` to add links to plain text.

### Tests

`tests/Darkspyre.Common.Markdown.Tests` parses links, autolinks and an attribute block with the pipeline and checks that
only web and mail links keep their targets and that no attribute reaches the HTML.

## Darkspyre.Agent.Common.UI

One library of shared Avalonia UI that any Darkspyre application can reference. It references no agent-ds project.
Each folder is its own namespace, `Darkspyre.Agent.Common.UI.<folder>`:

| Namespace | Holds |
| --- | --- |
| `.Controls` | `Forms` and `ButtonSize`, `Sizes`, `Badge`, `Spinner`, `LoopingAnimation`, `IconPicker`, `ColorPicker`, `StatusLight` and `StatusLightState`, `QuotaRing`, `ClipboardText`, `Watching<TShown>` and `UiChangeSubscription`, `Remount`, `KeyedViews`, `KeyedChildren`, `UiRefresh`, `ShownList` |
| `.Icons` | `Icon`, `IconSource`, `AppIcons`, `ProjectIcons`, `PluginPanelIcons`, `AttachmentPreviewIcons` |
| `.Dialogs` | `ModalLayer`, `Sheet`, `ConfirmDialog`, `PaletteSheet`, `DialogPresenter` with `SheetRequest`, `ISheet` and `ConfirmationRequest` |
| `.Errors` | `UiRegion`, `UiFailure`, `FailureView` |
| `.Composition` | `CompositionTarget`, `CompositionSlot`, `ICompositionPresenter`, `SurfaceScopeContext`, `AppearanceSection`, `IAppearanceSectionRegistry` |
| `.Themes` | `CommonTheme`, holding `ControlStyles` |
| `.Code` | `SourceView`, `DiffView`, `DiffViewMode`, and `TextDiff` with its line types |
| `.Markdown` | `MarkdownViewer`, `CopyFeedback`, the Mermaid renderer |
| `.Pdf` | `PdfViewer`, `PdfZoom` |
| `.Terminal` | `TerminalView` |

agent-ds's `Darkspyre.Agent.Plugin` references the library, so a plugin compiles against it and loads the
application's copy (agent-ds `SharedLibraries.props` excludes its runtime assets from plugin packages).

### Starting it

An application adds `new CommonTheme()` to its styles before its own. `CommonTheme` holds the common control styles,
the colour picker's templates, the palette, failure-card, quota-ring and status-light styles, and the
`CompositionTarget` template. Three static hooks connect the library to its application, each set once at startup:

| Hook | Receives | Without it |
| --- | --- | --- |
| `CompositionTarget.Presenter` (`ICompositionPresenter`) | Each target entering and leaving a window, and a request for each of its four inline slots | A target shows its own content and empty slots |
| `AppearanceSection.Registry` (`IAppearanceSectionRegistry`) | Each control joining or leaving a typography section | A section changes no font |
| `UiRegion.Log` (`Action<UiFailure>`) | Every failure a region shows, and every `UiRegion.Report` no region encloses (`UiRegion.NoRegion`) | Nothing is logged |

`ModalLayer.Attach(window)` makes Escape dismiss the topmost dialog of that window.

### Controls and dialogs

- **`UiRegion`** is one region of a window. A failure reported inside it through `UiRegion.Report`, or thrown while
  its content is laid out, replaces the region with a `FailureView` offering Reload this area and the error details;
  the rest of the window keeps working. A failure belongs to the `Scope` that was showing: another scope shows the
  content again, and the failed scope shows its card again until Reload this area. Its style class is `ui-region`.
- **`FailureView`** is the card a failed area shows: an optional title and subtitle, a sentence, the caller's actions
  with Show details, an optional status line, and the hidden details table and stack trace.
- **`Watching<TShown>`** is a view that reads a record from its owners and renders only when the record changed. It
  subscribes to each `Owners` entry, an owner's `SubscribeChangesAsync` method group as a `UiChangeSubscription`,
  while it is in a window. One refresh runs at a time, requests during a refresh coalesce into one more, and an equal
  record renders nothing. A failing read or render goes to `UiRegion.Report`.
- **`DialogPresenter`** shows a `SheetRequest` as a `Sheet` and a `ConfirmationRequest` as a `ConfirmDialog` in one
  window's `ModalLayer`, from any thread. The returned `ISheet` closes when disposed; its `Completion` is true only
  after `Complete()`. A confirmation answers true only for an explicit confirm; a cancelled token closes it and answers
  false. `ConfirmDialog.AskAsync` takes the same cancellation token.
- **`StatusLight`** is a round light for a `StatusLightState`, pulsing at a pace and offset derived from its key, with
  style classes `light-active`, `light-off`, `light-failed`, `light-fallback` and `light-busy`. **`QuotaRing`** is a
  14 px gauge whose fill and colour follow the used fraction, grey with a question mark when unknown, dimmed when stale
  and pulsing while updating. **`ClipboardText.WriteAsync`** writes literal text to the window's clipboard and returns
  false when no clipboard is available.

### Resource keys

The styles and controls read these application resources through `DynamicResource` or a resource lookup, so a theme
change restyles at once. agent-ds's theme supplies all of them.

| Keys | Type |
| --- | --- |
| accent.brush, bg.brush, color-accent-foreground.brush, color-accent.brush, color-background.brush, color-border.brush, color-border.brush.40, color-border.brush.60, color-destructive.brush, color-destructive.brush.90, color-foreground.brush, color-info.brush, color-input.brush, color-muted-foreground.brush, color-muted.brush, color-popover-foreground.brush, color-popover.brush, color-primary-foreground.brush, color-primary.brush, color-primary.brush.20, color-primary.brush.90, color-ring.brush, color-secondary-foreground.brush, color-secondary.brush, color-secondary.brush.80, color-success.brush, color-warning.brush, danger.brush, faint.brush, line.brush, muted.brush, panel.brush, raised.brush, text.brush, text.brush.3 | `IBrush` |
| danger, faint, line, ok, warn (`QuotaRing`) | `Color` |
| control-h, control-h-lg, control-h-sm, control-h-xs, font-size-body, font-size-code, font-size-control, font-size-heading-1 to font-size-heading-5, font-size-secondary, font-size-small, font-size-title | `double` |
| font-body, font-code, font-control, font-heading-1 to font-heading-5, font-secondary, font-small, font-title | `FontFamily` |
| radius, radius-lg, radius-md, radius-sm | `CornerRadius` |

The styles also restyle these Fluent resources: ComboBoxBackgroundBorderBrushUnfocused, ComboBoxBackgroundUnfocused,
ComboBoxDropDownBackground, ComboBoxDropDownBorderBrush, ComboBoxDropdownBorderPadding, ComboBoxDropdownBorderThickness,
ComboBoxDropdownContentMargin, ComboBoxPlaceHolderForeground, OverlayCornerRadius and TextControlPlaceholderOpacity.

### Document viewers

Each viewer is a `TemplatedControl` with its own API; the third-party library behind it is an implementation detail,
so replacing one changes one folder and no caller. Each viewer carries its own styles, so a host adds nothing to its
application. Colours and fonts come from these resource keys when the host supplies them, and
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

- **`MarkdownViewer`** renders Markdown through LiveMarkdown with the policy of `Darkspyre.Common.Markdown`. Raw HTML is
  shown as text; generic attributes and media embeds are off. A link or image target that is not an absolute http or https address with a host, a mailto address
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
  ([DSA-1102](https://darkspyre.atlassian.net/browse/DSA-1102)). The pipeline is `MarkdownSafety.CreateBuilder` with
  the viewer's Mermaid fences, its link-source links and LiveMarkdown's code-block span fixer, then `MarkdownSafety.Build`.
- **`SourceView`** shows read-only text in AvaloniaEdit with line numbers and TextMate highlighting chosen by file
  extension or language id. `Find` marks every case-insensitive match; `FindNext` and `FindPrevious` wrap. `GoToLine`
  scrolls to a line; asked for before the text is set, it applies when the text arrives. A new `Text` keeps the
  scroll position. The highlighter exists only while the view is in a window.
- **`DiffView`** compares `OldText` and `NewText` with `TextDiff` off the UI thread, once per pair of texts, with three
  lines of context. `ViewMode` chooses the layout. `Unified` (the default) shows a header per hunk, old and new line
  numbers and plus and minus marks in one column. `SideBySide` shows the old text on the left and the new text on the
  right, aligned row for row: context on both sides, the removed and added lines between two context lines paired by
  position, and empty filler rows on the shorter side. Each column has its own line numbers, and scrolling one scrolls
  the other to the same row. Both layouts shade added and removed rows and show a "No final newline" note. `OldLabel`
  and `NewLabel` name the two sides: they head the two columns side by side, and form one "old → new" line above a
  unified diff. A null text is an absent side and an empty string an empty file: side by side, an absent side shows its
  label and "File is absent in this version." instead of rows; unified, both compare as empty. Line endings take part
  in the comparison. Equal texts show "No text differences."; a comparison over the work limit (4 million table cells)
  shows its error. Changing `ViewMode` redraws the last comparison without computing it again. `Find` marks every
  case-insensitive match and `FindNext` and `FindPrevious` wrap, as in `SourceView`. Side by side, one match list runs
  over the rows in order, the old side's matches before the new side's, and a row both columns show identically
  counts its matches once. `TextDiff` and its line types are public for callers that compare without a view.
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

## Darkspyre.Mobile.Components

A Razor class library (`net11.0`, no MAUI) of what every Darkspyre mobile app's screens share. It references
`Darkspyre.Common.Markdown` and `Microsoft.AspNetCore.Components.Web`. Its web assets are served under
`_content/Darkspyre.Mobile.Components/`.

| Item | Holds |
| --- | --- |
| `MarkdownView` | Renders Markdown as HTML for transcript entries, streaming text and prompt messages |
| `wwwroot/mobile.css` | The mobile theme: light and dark colours, `header`, dense `list` and `row`, `empty`, `card`, `button`, `input`, `icon`, `error`, the Markdown styles and the code colours |
| `Icons/` | Lucide icons as inline SVG components (`Bell`, `ChevronLeft`, `ChevronRight`, `Folder`, `LogIn`, `MessageSquare`, `Monitor`, `RefreshCw`, `Send`, `TriangleAlert`, `X`) drawn in the current text colour; `Class` adds classes after `icon` |
| `wwwroot/highlight/` | highlight.js 11.11.1, the common-languages build, and its licence |
| `wwwroot/markdown.js` | `highlightNew(element)` |

### Starting it

The app's host page loads the theme and the two scripts before the Blazor script:

```html
<link rel="stylesheet" href="_content/Darkspyre.Mobile.Components/mobile.css" />
<script src="_content/Darkspyre.Mobile.Components/highlight/highlight.min.js"></script>
<script src="_content/Darkspyre.Mobile.Components/markdown.js"></script>
```

A screen writes `<MarkdownView Text="@text" />`.

### Behaviour

- `MarkdownView` parses with `MarkdownSafety.CreateBuilder` and a link source that accepts and finds nothing, plus one
  rule of its own: every image becomes its alt text, so no remote image loads. Only http, https and mailto targets stay
  links; every other link or autolink target, relative ones and `javascript:` included, shows as its own text. Raw HTML
  shows as text, and no attribute, style or embed reaches the page. The HTML is parsed again only when `Text` changes.
- After each render, `MarkdownView` calls `highlightNew` on its element. The function colours each `pre code` block not
  yet coloured whose `language-<name>` class names a language highlight.js knows. A block in an unknown language, or one
  highlight.js fails on, stays plain text, and a missing or failing script leaves every block plain; no error reaches
  the renderer. highlight.js wraps text in `span` classes and runs no content.
- A tapped link navigates the web view unless the host keeps it on the app's pages; a MAUI app uses
  `UseDarkspyreLinkPolicy` from `Darkspyre.Mobile.Maui`.
- The desktop keeps plain code blocks; only the mobile view colours them.

### Tests

No automated test. Its one rule beyond the shared policy, images as alt text, is checked by hand with the code colouring
and the link rules in a MAUI host on the iOS simulator.

## Darkspyre.Mobile.Maui

A .NET MAUI library (`net11.0-ios`) with what every Darkspyre MAUI app needs. It is
built by `Darkspyre.Mobile.slnx`. It references `Darkspyre.Mobile.Components`, `Darkspyre.OAuthBroker.Client` and
`SecureStore.iOS`, and imports the oauth-broker and SecureStore `eng/SourceReferences.targets` in its own project file.
It compiles against `Microsoft.AspNetCore.Components.WebView.Maui` without that package's build targets, which belong to
the app: they would make the library a web app with its own `_framework/blazor.modules.json`, colliding with the app's.

### Starting it

```csharp
builder.UseMauiApp<App>().AddDarkspyreMobile("com.example.app"); // the app's own stable credential service name
blazorWebView.UseDarkspyreLinkPolicy();
```

### Behaviour

- **`AddDarkspyreMobile`** adds the Blazor WebView and the platform's `ICredentialStore` for the service name. The
  platform registration is the partial method `AddPlatformCredentials`, which each `Platforms/<platform>/` folder
  implements; on iOS it is SecureStore's `IosKeychainCredentialStore`, the app's device-only Keychain. No shared file
  names an operating system. MAUI's `SecureStorage` is not used.
- **`UseDarkspyreLinkPolicy`** keeps a `BlazorWebView` on the app's own pages. A URL outside the app's origin is
  cancelled; an http, https or mailto URL is then opened with `Launcher.Default`, so a tapped web link opens in the
  system browser. Every other scheme, including `tel:`, other apps' schemes and the app's own redirect scheme, opens
  nothing.
- **`WebAuthenticatorSignIn`** runs `BrokerSignIn`'s browser step through MAUI's `WebAuthenticator`; one instance is
  both its `IBrowserLauncher` and its `ISignInCallbackFactory`. Its callback's `RedirectUri` is the redirect it was
  created with. `OpenAsync` starts the system sign-in sheet on the main thread without waiting for it, and `WaitAsync`
  returns the redirect's query parameters. A sheet the person cancels makes `WaitAsync` throw
  `OperationCanceledException`. The token passed to `WaitAsync` stops the wait but cannot dismiss the sheet.

### Tests

No automated test. The Keychain write and read through `ICredentialStore`, the link policy opening an https link in
Safari, and the window's `Stopped` and `Resumed` events are checked by hand in a minimal MAUI host on the iOS simulator.
An app built with the iOS 27 SDK declares the UIKit scene life cycle: a `SceneDelegate` derived from
`MauiUISceneDelegate` and a `UIApplicationSceneManifest` in `Info.plist`, as the MAUI templates do.

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
  removes it.
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
- `RemoveAsync` takes a workspace folder or a single worktree and removes it whatever it holds. For each worktree it
  unlocks it, removes it with `ForceRemoveWorktreeAsync`, and deletes its local branch while the branch still names the
  head the worktree had, logging that head commit so `git branch <name> <commit>` recovers it until Git collects it.
  A workspace folder is named after the branch it was created on, so when a worktree was switched to another branch,
  `RemoveAsync` also deletes the branch named like the folder, logged the same way, unless a worktree has it checked
  out. Then it deletes the folder with whatever remains in it, such as `tmp` and `.agent-ds`. Uncommitted and untracked
  files and commits on no remote branch go with it. A detached worktree and a branch with no commits have no ref to
  delete. A failure stops the removal with an `IOException` and leaves the rest in place
  ([DSA-1203](https://darkspyre.atlassian.net/browse/DSA-1203)).
- `RepairAsync` stashes each healthy worktree's uncommitted and untracked changes (`darkspyre-repair <timestamp>`);
  moves each broken repository folder into `tmp/repaired-<yyyyMMdd-HHmmss>`; unlocks each missing worktree's record and
  prunes the repositories; re-creates each missing worktree on the branch (the local branch, else a new branch at
  `origin/<branch>`, else a new branch at the base, fetched first); locks every configured worktree, keeping existing
  locks; moves every stray item, folder or file, into the repair folder, and leaves another worktree where it is and
  reports it; then re-applies each stash by its commit and
  drops it only when it applied cleanly. A conflicting stash stays, the worktree keeps its conflicts, and the result
  names the repository, the paths and the stash commit. Nothing is deleted.

### Notes for callers

- The library does not track which workspaces an owner created. When a session ends, the caller removes every
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
Every installation selects its build as preferred and then removes each other cached build that no launch or running
service pins. A subscriber whose build was removed copies it in again when it next installs.

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
