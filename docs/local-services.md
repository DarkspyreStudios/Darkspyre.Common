# Local service contracts and lifecycle

`Darkspyre.LocalServices.Contracts` defines independent installation and subscriber interfaces, shared result/launch types,
service identities and typed control/business messages,
wire limits and lifecycle deadlines. It depends on the .NET libraries only. Protocol version 2 is
independent of the SDK/plugin version. `Darkspyre.LocalServices` contains the frame codec and the
subscriber registry, portable connector, handshake and immutable package-cache policy. The build-selected
platform assemblies own native files, transport and detached launch. Neither library
opens databases.

## Frames

A frame contains a four-byte unsigned little-endian length, a one-byte kind and a payload. The length counts
the kind byte and the payload, and is between 2 and 1,048,577 bytes, so a payload holds at most 1,048,576 bytes.
The codec validates it before allocating the payload. A clean EOF before a header returns null. A truncated
header or payload throws, and so does an unknown kind.

Kind 0 carries a UTF-8 JSON message. Unknown message kinds, unknown fields, missing constructor fields and
duplicate properties fail decoding. Enum values use camel-case strings.

Kind 1 carries one `BlobChunkMessage`. Its little-endian header is Epoch (16 bytes), RequestId (16),
TransferId (16), Sequence as a 32-bit integer, TotalBytes as a 64-bit integer, Final as one byte (0 or 1)
and, when Final is 1, the 32 raw SHA-256 bytes of the whole body. The header is 61 or 93 bytes. The raw chunk
bytes follow it. A chunk has no JSON form. Chunk data is limited to 1,048,483 bytes in either direction, so
one chunk fills one frame. Raw bytes avoid the base64 growth and parse of a JSON chunk.

`ServiceFrameCodec.WriteAsync` bounds serialization before sending the header. The connection owner must serialize writes and
must discard the connection after any failed or cancelled frame read/write. It must not retry a
partial frame. Chunk order, total transfer size, digest, epoch, context and message-state validation
belong to the connection/operation owner; decoding a frame does not grant admission or authority.

`AcceptedMessage` acknowledges admission, not commit. `OutcomeUnknown` distinguishes a lost or
uncertain mutation outcome from a confirmed failure. Authentication material is not part of these
message records. Error envelopes contain a safe code and correlation ID, not exception/provider text.

## Subscriber lifecycle

`SubscriberRegistry` owns admission and lease timestamps under one lock. A connection ID represents
an authenticated connection. It does not represent a project, worktree or worker. Each operation
returns the resulting state and removed identities. The connection owner must revoke the authority
of every identity in `RemovedSubscribers`, including removals returned by a rejected operation.

The states are Starting, Active, Draining and Exited. The first subscription must arrive within 15
seconds. A successful first subscription enters Active. Heartbeats refresh an existing lease for 20
seconds. Duplicate subscriptions and ordinary business admission checks do not refresh the lease.
An expired lease cannot be revived by a heartbeat. Another surviving subscriber keeps the registry
Active. The final removal enters Draining immediately. A new subscription cannot revive Draining or
Exited. Concurrent last-removal/admission calls serialize through the same lock.

The host must call `Tick` independently of business processing. The registry uses `TimeProvider`
monotonic timestamps. Tick reports a graceful-drain deadline after 10 seconds and a total shutdown
deadline after 15 seconds. It does not terminate processes or pretend cleanup completed. The host
calls `CompleteShutdown` after cleanup and retains OS/database ownership until that cleanup ends.
`BeginDrain` fences all subscribers when the backend cannot continue.

## macOS ownership

`MacPrivateDirectory` walks an absolute path through retained directory descriptors. It rejects
symlinks and traversal components, requires an owner-only leaf directory without extended ACLs,
and rejects nonlocal filesystems and known guest/network filesystem types. These checks cannot
detect a host-local directory exported by an unrelated VM-sharing configuration.

`MacFileLease` uses Darwin `flock` on a permanent private file. Creation uses an exclusive atomic
rename from a descriptor-relative private temporary file. It never truncates or replaces an existing
lock file. Contention returns null; filesystem and permission failures throw. Closing the lease
releases ownership but leaves the file and its inode in place. Regular files must have a single link.
Family/store acquisition order and database lifetime belong to the service owner, not the lease.

The AgenticTools test project contains focused framing, lifecycle and macOS ownership tests. A disposable
Python subprocess observes the same OS lease and exercises owner death without touching SQLite.

## macOS socket transport

`MacServiceOwner` acquires the family lease before the store lease. A losing family contender does
not create another store. An independently owned store raises `StoreInUseException` and releases the
new family lease. The hosting layer must close all database handles and peers before releasing the owner.

`MacServiceListener` publishes one epoch per owner lifetime. Discovery includes family, protocol,
store, build digest and layout. Its random 256-bit capability is in a separate 0600 file. Both files
are published atomically through descriptor-relative private temporary files. Endpoint names derive
from the epoch; discovery cannot supply arbitrary socket or capability paths. A new exclusive owner
can remove the previous epoch's published endpoint. Socket paths that exceed Darwin's limit fail;
there is no temporary-directory fallback.

Server and client verify the peer OS user. Authentication sends the epoch and capability in a fixed
48-byte prelude, followed by bounded Hello/Welcome frames. Capability comparisons use constant-time
comparison; temporary secret buffers are cleared. Negotiation checks service, protocol, store and
build identity. The first handshake has a deadline and pending server handshakes have a fixed cap.
Discovery contains no capability value. OS-user authentication does not distinguish malicious code
already running unrestricted under that same user account.

`MacServiceClient` connects to an existing owner. Authentication returns a `ServicePeer`, not a
subscription or file permission. The caller must still admit the subscription and every operation.
Closing a listener cancels pending accepts and removes only its own published epoch while ownership
is held. Accepted peers belong to the hosting layer and are disposed separately.

## Detached launch

`MacDetachedLauncher` accepts a library-resolved installed executable specification, verifies its SHA256
and trusted regular-file permissions, and uses `posix_spawn` with a new session and close-on-exec by
default. It does not execute a shell or resolve an executable through PATH. Standard descriptors point
to `/dev/null`; the backend owns its diagnostic sink. The child starts in `/` with only explicit HOME,
TMPDIR, PATH and LANG values. Caller provider credentials, build variables and dynamic-loader overrides
are not inherited. A pinned package launch adds only the cache identity, build digest and inherited
lease descriptor markers. Termination signals have default dispositions and an empty inherited mask.

The returned observation reports exit code or termination signal and reaps the child without owning
its lifetime. It exposes no Kill or Dispose operation. A launch-spec digest mismatch prevents execution.
Launch arguments are bounded and are trusted registration data, not model tool arguments.

## Immutable service packages

`ServicePackageCache` copies a complete manifest-verified payload into a content-addressed build
folder outside plugin-generation staging. The manifest binds the entry point, empty directories,
file sizes, executable flags and SHA256 digests. Paths must be normalized and root-relative without
symlinks or case aliases. Registration supplies the expected manifest digest; capturing an inventory
does not establish trust by itself.

Installation verifies the source, copies into a private `.install-<id>` sibling directory, verifies the copy, seals its directories
and executables to 0500 and data files to 0400, and publishes with an exclusive atomic rename in the same parent. A cross-parent rename requires
write permission on the moved directory on macOS, so staging shares the final build parent.
Payloads are independent copies, not hardlinks to plugin files. Existing builds are verified and
never repaired in place. An explicit collector removes abandoned staging under the catalog lease.
Windows creates each resource with its owner and protected DACL. Sealing and collection change only
the DACL. A sealed owner's right to change its DACL does not grant permission to rewrite ownership.

The catalog lease serializes install, selection, pin acquisition and collection. A shared permanent
per-build lease pins a verified payload. Collection requires its exclusive counterpart and refuses
the preferred build. `ServiceInstaller` collects under the same catalog lease right after it selects the installing
build as preferred: every other build whose exclusive lease is free is removed, so the cache keeps the preferred build
and the builds that launches or running services pin. Lock files remain permanent after payload collection. A pin outlives the cache
object that created it. Preferred selection is atomic and only changes future launch selection.

A pinned Unix launch duplicates its lease into child descriptor 3 before exec. The macOS spawn action
explicitly clears close-on-exec on that destination, including when an adopted descriptor is already 3. The backend must call
`AdoptLaunchPinAsync` before discovery publication or storage access and retain the returned pin for
its lifetime. Adoption verifies the descriptor identity and complete cached payload, clears the
launch markers and marks the descriptor close-on-exec. Failure aborts startup; it never silently
acquires a replacement pin. The inherited descriptor protects the payload even if the launcher exits
before managed backend startup. Read-only modes and advisory leases coordinate cooperating clients;
they do not prevent unrestricted same-user code from modifying files.

Search uses these libraries for its production service and subscriber connections. Startup belongs to
the subscriber library. Harness Host has no service registration or launch responsibility.

`Darkspyre.LocalServices.TestBackend` is a disposable test executable. It adopts a launch pin, signals
readiness through its test coordination directory, and exits on release or a bounded timeout. The
Memory test project copies its build output for the cross-process package-retention test. It opens
no databases and is not a production backend or an installed feature.

## Live control connections

`ServiceControlHost` connects authenticated peers to one `SubscriberRegistry`. Subscribe/Ack establishes
admission before Ping/Pong renews it. Wrong epochs, duplicate subscription and out-of-direction messages
fail. EOF and Unsubscribe revoke immediately. A timer expires leases independently of peer receive loops.
The host reports `Draining` when admission closes; it cannot revive that epoch. Terminal notifications
have serialized bounded writes. Failed or cancelled frame operations close the stream without replay.

`ServiceSubscription` owns the client peer after SubscribeAck. It sends heartbeats every five seconds,
reads control responses concurrently, and closes if responses stop for twenty seconds. Disposal sends
Unsubscribe within a bounded write, then closes and joins its receive/heartbeat loops. Its `Closed` task
reports the safe disconnect reason. It has no reconnect, process-kill or business-replay operation.

Connections carry control messages, business requests, results, chunks, invalidations and callbacks. `ServiceControlHost`
does not open stores, release the family owner, dispose the listener, or mark domain cleanup complete.
Its `Draining` task lets the backend begin its own cleanup while ownership remains held. Database cleanup
is not implemented by these control components.

## Backend cleanup deadline

`ServiceProcessShutdown` runs only inside a standalone backend process. It attempts each named cleanup
stage even when an earlier stage throws, and returns completed stages and failures for backend diagnostics.
Its token requests cancellation after ten seconds. A dedicated watchdog thread exits the current process
with code 70 at the fifteen-second total deadline. The thread does not depend on a blocked cleanup stage
or a free thread-pool worker. Code 70 is abnormal shutdown, not successful cleanup.

The backend calls this coordinator immediately after admission drains and retains family/store/package
ownership through resource closure. The client application must never call this process-terminating API.
The test backend exercises a synchronously blocked cleanup stage with an accelerated test clock. That test
verifies abnormal process exit and OS release of the inherited package pin; it does not validate SQLite
closure or corruption recovery.

## Automatic connect and launch budget

`ServiceConnector` takes a trusted installed registration and a subscriber-library launch observer.
It authenticates and subscribes to an existing protocol/store/layout-compatible owner even when another
package is preferred. A store mismatch, authentication, permissions and malformed metadata fail closed.
Only transport loss before subscription returns and explicit Unavailable/ServiceDraining responses retry.
An IncompatibleProtocol answer (another family or protocol in discovery, a handshake or a layout mismatch) is
treated as no owner: the loop reserves and launches as usual. Discovery can outlive an owner that ran another
protocol, and the launched owner replaces it when it publishes. When the connect deadline passes and the last
answer was IncompatibleProtocol, the connector throws that error, not Unavailable, so a live owner of another
protocol is still reported. No business request is part of this loop. Cancellation never kills a backend or revokes another client.

When no owner is available, the connector reserves a launch, pins the preferred verified package and
spawns its registered arguments. The child inherits the pin. The observation callback must consume
completion, including launch-accounting failures; it does not acquire process-kill authority.

`ServiceLaunchBudget` serializes `launches.json` through permanent `launch.lock` in the private family
runtime directory. It probes `owner.lock` without waiting or stealing it. A pending reservation prevents
duplicate launches for the first-subscriber window. At most three failed/in-progress attempts fit in a
sixty-second window. Exhaustion reports Unavailable. Explicit subscriber-library retry resets accounting,
not ownership. Normal exits remove their attempt. The backend marks its attempt running after ownership
and records clean shutdown before relinquishing ownership; the launching host also observes exit.
The backend record keeps normal accounting correct when its launcher is gone.

Persisted launch-window timestamps use UTC across processes. A clock rollback fails closed until the
recorded window passes or explicit retry resets it. Subscriber and shutdown deadlines stay monotonic.
Accounting records do not grant ownership. Corrupt/non-private accounting fails instead of being replaced.
The disposable service fixture composes the owner, listener, control host, package pin and shutdown
coordinator without opening databases. The fixture echoes complete validated request bodies through the common dispatcher.

The control host accepts the readiness timestamp after initialization and publication. The first-subscriber
window begins when clients can subscribe.
The process fixture includes an application-launcher mode that establishes its own subscription and exits
without managed disposal on a test signal. A second application's connection retains the backend and
its package lease. After its last release, the backend records normal exit without the original launcher.

## Request admission state

`ServiceOperationRegistry` tracks admitted connection identities and immutable operation allowlists under
monotonically increasing connection-local context revisions. Trusted backend/host registration supplies
these allowlists; request payloads cannot grant themselves authority. Domain adapters still enforce source,
file and account permissions. The registry snapshots request payloads and stamps the authenticated
connection identity. Duplicate active IDs fail. Independent connections have independent ID namespaces.

`ServiceRequestContext.RemainingTime` subtracts elapsed admission-clock time from the request deadline
and clamps the result to zero. Body transfer consumes the same budget as the handler. The property
creates no timer and changes neither cancellation nor publication authority.

Admission fails Busy at 32 active requests per connection or 256 per family. Cancel, deadline, context
revocation and connection closure signal cooperative stop without releasing a work slot. The dispatcher
must dispose its lease only when work settles. Cancel does not prove rollback. A stop reason records the
first signal; later revocation still invalidates publication authority independently.

`ServiceControlHost.Operations` shares the lifecycle serialization lock. Subscription admits a connection;
EOF, expiry and drain remove its authority immediately. Admission checks also advance lifecycle expiry,
so a delayed timer cannot admit expired authority. Deadline/stop notifications use asynchronous task
continuations and never execute a handler while the serialization lock is held.

`CanPublish` is an instantaneous authority check, not a transferable permission. Final result publication
must be serialized with revocation by the dispatcher and fenced again at the client host. The common dispatcher uses the configured operation allowlist at context revision 1. It adds no original-file
permission and exposes no Harness callback or generation binding.

## Build boundary

Darkspyre.Common supplies the standalone MSBuild configuration. Its `build/LocalServices/ServicePlatform.props`
selects the target platform; each project retains its assembly name.
These projects compile without Harness imports, plugin packaging or Host dependency resolution.
The contracts and lifecycle framework use .NET references only. Debug symbols use portable PDBs;
repository metadata identifies Darkspyre.Common.

## Portable installation and ownership

`IServicePlatform` supplies installation, subscriber-native operations and backend ownership. The shared
`ServiceConnector` owns the connect/reserve/pin/launch loop and exit accounting. Its subscription owns no
process termination handle. Each build-selected platform composition exposes its own `ServicePlatform`.

The subscriber library calls `InstallAsync` with a trusted bundled manifest and its expected digest. The
shared installer holds the existing cache catalog lock while installing, creating or reading
`registration.json`, and selecting the verified package. Concurrent installers retain one StoreId.
An interrupted preferred selection reuses that registration. A different LayoutId fails explicitly.
Installation opens no database and starts no process. Host supplies no factory or registration.

The macOS driver resolves durable resources beneath the user's Library/Application Support/Darkspyre
application directory. Rendezvous uses the shorter Library/Caches/Darkspyre application directory.
`TryOwnAsync` adopts the inherited package pin and acquires family ownership before store ownership.
The returned owner prepares private store/staging directories. The backend initializes storage before
`PublishAsync`. After admitted work and peers close, `CompleteAsync` removes discovery, records a normal
exit and releases ownership. Failed-start disposal never records normal exit.

`ServiceFileSystem` and `ServiceDirectory` are internal native-file seams. Cache verification, copying,
selection, collection and launch-budget state each have one implementation in the neutral framework.
Native permission checks, permanent leases, atomic directory publication and inherited pin adoption
belong to the selected platform assembly.

## Service callbacks

`ServiceControlHost.CallbackAsync` asks one subscriber connection to run a named operation and returns its answer. The
backend names the connection and the active request the callback serves. A callback is one `CapabilityRequestMessage`
with a JSON payload and a deadline, answered by one `CapabilityResultMessage`; it carries no body transfer, so its
payload and answer must each fit in one frame. A subscriber answer over the frame bound is replaced by Failed with
InvalidFrame before it is sent.

The subscriber runs `IServiceSubscription.CallbackHandler` on its own task, so the receive loop keeps reading. Without a
handler the callback fails with InvalidState; a handler exception fails it with Unavailable. A deadline or the caller's
cancellation makes the service send a `CancelMessage` whose request ID is the callback ID and return Cancelled at once.
The subscriber then cancels the handler's token. A result that crosses that Cancel on the wire is ignored. A connection
that is gone, or closes before it answers, fails the callback with Unavailable. Subscription disposal cancels running
handlers and joins them.

## Business bodies and invalidations

`ServiceSubscription.InvokeAsync` sends one request with a fresh request ID and body transfer ID. The
caller owns its input stream. Accepted acknowledges admission; the client then sends bounded chunks.
`ServiceControlHost` validates sequence, total length and final SHA-256 before entering its handler.
Request spools live under the owner's private staging directory and are deleted when dispatch settles.

The single receive loop demultiplexes control, body and request messages. Writers hold their semaphore
for one frame, allowing heartbeat and cancellation traffic between chunks. Result chunks precede the
terminal result reference. The client rejects incomplete or mismatched transfers and never returns
partial bytes as success. Service invalidations use the same subscription's `EventReceived` event.

Cancellation before handler entry returns Cancelled after the admitted transfer settles. A handler
that stops after entry without a proven result returns OutcomeUnknown. A proven domain result remains
valid after caller cancellation. Lost transport reports an unknown submitted outcome; read adapters
interpret that transport failure as unavailable. There is no automatic business replay. The registry
slot remains occupied until the handler, body transfer and cleanup finish. Connection disposal joins
that work before native ownership can be released.

## Linux ownership and launch

The Linux driver uses private descriptor-relative files and permanent flock leases. Its Unix socket
checks SO_PEERCRED against the effective user before the shared capability handshake. Detached
launch uses glibc posix_spawn with a new session and retains only null standard streams and the
inherited package lease. The native ABI implementation supports Linux x64 and ARM64 with glibc.
File inspection translates each architecture's `stat` layout into the same device, inode, link-count,
mode and user fields. Directory and no-follow open flags also use the architecture's ABI values.

Data uses XDG_DATA_HOME or the user’s .local/share directory. Rendezvous uses an owned
XDG_RUNTIME_DIR when available, otherwise the private data directory. Cache, registration, launch
accounting and subscriber policy use the same neutral implementations as macOS.

## Windows ownership and launch

The Windows driver uses protected current-user/SYSTEM file DACLs, rejects reparse points and checks
opened file identity. Family, store, catalog and launch ownership use LockFileEx on permanent files.
Package pins use read handles that permit only shared reads. Collection requires write access;
an inherited child handle therefore retains the pin after launcher exit. Adoption checks the permanent
file identity and clears inheritance without closing and reopening the handle.

The pipe endpoint uses an explicit current-user DACL and denies network access. The server compares
the impersonated TokenUser SID after reading the authentication prelude; the client checks the server
process TokenUser before sending it. Shared capability and protocol checks then establish the peer.
The pipe name uses the application's runtime parent name and the current user SID hash.

CreateProcess receives a verified cached executable, a clean environment, null standard streams and
an explicit handle list containing the package pin. It creates a detached process group and requires
breakaway when the launching process belongs to a job. Failed independent launch reports unavailable.
Only the internal exit observer retains the process handle; subscriber disposal cannot terminate it.

## Native qualification fixture

AgenticTools tests select the same native platform as the test backend at build time. They exercise
concurrent installation, first/last subscribers, a package update with a live owner, launcher exit
with another subscriber, and inherited pin retention before managed child adoption. Request dispatch
checks use real framed streams. Cross-composition builds use separate artifact directories so restore
assets from one platform do not enter another platform's backend payload. The test project queries
the backend target path and refreshes its copied payload from that output directory. The pin handoff
fixture publishes its readiness PID through a rename after the complete write.

The fixture is framework dependent. Detached execution requires the matching .NET runtime in an
installation the apphost can discover. Invoking the test runner through a user-local dotnet executable
does not register that runtime for a detached apphost.

The sibling Search test project also exercises typed storage calls and independent invalidation readers
through real LocalServices frames. Its test-only friend access creates paired authenticated stream fixtures.

The Search build packager reuses the internal package capture path through a friend assembly.
Its platform composition supplies the same executable-file inspection used during installation.

## Subscriber packaging

`build/LocalServices/SubscriberPackage.targets` bundles a service with its subscriber library. The subscriber project
sets `LocalServiceProject`, `LocalServiceEntryPoint`, `LocalServiceMetadataPrefix`, `LocalServiceLayoutId` and
`LocalServicePackageFolder`, then imports the targets. Before the subscriber compiles, the targets build the packaging
tool in the subscriber's configuration, so a Release build of a solution that does not contain the tool finds it in
`bin/Release`. The tool's `capture`
command inventories the built service output and writes `manifest.json` and `ServicePackageMetadata.cs`, which carries
the `<prefix>.LayoutId` and `<prefix>.ClientBuildDigest` assembly metadata. The client digest covers the manifest and
the hashes of the subscriber's compile inputs and references. The payload is copied to `<folder>/payload/` and the
manifest to `<folder>/manifest.json` beside the subscriber, for builds and no-build publishes.

A repository whose projects reference a service's libraries or receive a subscriber's payload imports
`build/LocalServices/LocalServiceConsumer.targets` once, from its `Directory.Build.targets`. Agent-ds, AgentDSPlugins,
Inference and Search do, and Darkspyre.Common does for its own libraries' references. The file applies to every project
of the repository:

- A solution build unsets Configuration and Platform for a referenced project outside the solution, which would build
  the LocalServices libraries in Debug under a Release consumer. The file sets
  `ShouldUnsetParentConfigurationAndPlatform` to false, so those libraries build in the consumer's configuration.
- After each build, `RemoveEmptyLocalServiceDirectories` removes folders without files from `<folder>/payload/` in the
  project's output, because incremental clean deletes files of an earlier payload layout but not their folders. The
  folder is the project's `LocalServicePackageFolder`, or `service` in a project that only copies the payload.

The folder's last segment must be `service`: the packaging tool's `finalize` command, run by Mac signing, refreshes every
`service/manifest.json` from its signed `payload/`.

## Wire documentation

The Contracts library emits XML documentation for wire envelopes, fields, lifecycle states and safe errors.
The runtime library emits documentation for subscriber leases, request admission, package pins and shutdown.
The macOS implementation emits documentation for native directory identities, lease ownership and connection setup.
The SDK reference reads this output alongside its assembly. Protocol values and message shapes remain unchanged.

## macOS fixture signing

The test backend executable uses Apple Development certificate
`96BA98550C1A060322A9B7A217D81A07A2C39BED` and the fixed identifier
`com.darkspyrestudios.localservices.tests.backend`. Sign the copied AgenticTools test output before
running its native lifecycle cases. The fixtures capture their package manifests from those signed bytes.
The test runner uses `/Volumes/Data/tmp/signed-test-host/dotnet` with identifier
`com.darkspyrestudios.agent.tests.testhost` and team `L469XNMGLQ`.
