# NMBV Windows Commander Rescue v2 — design

## Goal

Evolve the current Windows Commander MCP into a small, independently bootable,
high-confidence rescue/control plane for Windows and for NMBV/Serena recovery.

The design must improve throughput, observability, failure isolation, and repair
capability without turning Windows Commander into a second Serena, a second
workflow engine, or a general autonomous coding agent.

The primary engineering intelligence remains NMBV Bot / Serena. Windows
Commander Rescue exists to keep machine-level control available when Serena,
its language server, its router, or its coordinator is degraded or unavailable.

## Design principles

1. **Independent rescue path.** Windows Commander must start and remain useful
   when Serena, NMBV Bot, its router, coordinator, language servers, checkpoint
   state, or task ledger are completely unavailable.
2. **Small failure domain.** A failure in optional CodeIntel, search acceleration,
   the terminal observer, or Serena integration must not terminate the core MCP server.
3. **Deterministic before intelligent.** Recovery decisions are rule-based,
   observable, bounded, and testable. The rescue MCP does not contain a second
   LLM agent.
4. **Bounded concurrency, not worker proliferation.** Parallelism is allowed only
   where operations are independent. Desktop control and conflicting mutations
   remain serialized.
5. **Truthful contracts.** Tool descriptions, result models, completion states,
   limits, and protocol negotiation must match actual behavior.
6. **Evidence before mutation.** Repairs are based on current process/file/runtime
   evidence. Mutations use compare-and-swap style preconditions where practical.
7. **Safe degradation.** Optional performance or CodeIntel features always have a
   simpler fallback or fail closed without taking down the control plane.
8. **No false zero-defect claim.** Completion means zero known P0/P1/P2 defects in
   the defined scope after adversarial testing, with remaining lower-severity
   risks documented.

## Current baseline

Authoritative reviewed source baseline:

- Repository: `xRaisenx/windows-commander-mcp`
- Commit: `71daff5af3962134c800d1bf5406a45e34e8279c`
- Published hardened executable currently used by the managed runtime:
  `D:\DEV\nmbv-tools\windows-commander-mcp-71daff5\WindowsCommander.McpServer.exe`
- Runtime alias: `nmbv-windows-commander-final`

Observed baseline test result on 2026-10-07:

- 33 passed
- 1 failed
- 34 total
- remaining failure:
  `AutomationServiceTests.VisionService_ResolveCaptureGlowBounds_PrimaryScreenFramesThatScreen`
  under the current non-interactive/headless execution environment.

## Confirmed problems to close

The implementation must close the following known defects or contract gaps before
Rescue v2 is considered complete.

### Request execution and protocol

- The stdio loop processes one request to completion before servicing the next.
  A slow operation therefore head-of-line blocks unrelated fast requests.
- Protocol negotiation currently echoes a client's requested protocol version
  rather than selecting from explicitly supported versions.
- The server owns a hand-written protocol loop. It needs either a proven MCP SDK
  boundary or a hardened compatibility layer with explicit protocol tests.
- Long-running work has no durable operation model exposed by Windows Commander.

### Process execution

- `execute_process(wait_for_exit=true)` currently returns process id `0` after
  successful completion instead of the actual launched PID.
- Timeout handling discards partial stdout/stderr evidence.
- Fire-and-forget process starts return a PID but do not provide a durable
  Windows Commander operation/session for status, output paging, or cancellation.
- Completion of process termination is not always verified before returning a
  successful result.

### Filesystem behavior

- `read_file` without a limit may materialize the entire file before the MCP
  response-size guard can reject it.
- Writes are direct rather than staged/atomic.
- There is no stale-read protection before overwriting a file previously
  inspected by an agent.
- Recursive copy/delete work is not cancellable at each item.
- Reparse-point/junction traversal policy is not explicit.
- The tool schema advertises `recycle`, but the service throws
  `NotSupportedException`.
- The `read_file` description claims encoding detection although the
  implementation currently defaults to UTF-8 or an explicitly requested
  encoding.
- `get_file_properties` claims a security descriptor summary that the returned
  model does not currently contain.

### Search and large-result behavior

- Content search performs sequential recursive filesystem traversal.
- Search has no cursor/continuation model.
- Process/window/service/application listings are not uniformly bounded or
  pageable.
- Result limits are sometimes applied only after expensive work has already been
  performed.

### UI automation and vision

- UI tree reads can traverse large trees before response-size limits are known.
- `find_ui_element` obtains and materializes a complete tree before filtering.
- Cached `AutomationElement` references have no bounded lifetime/eviction
  contract.
- Screen/OCR allocations are not protected by an input pixel budget before
  allocating the source bitmap.
- `max_dimension` needs an upper bound.
- Desktop automation requires a single STA/exclusive execution lane when the
  general request dispatcher becomes concurrent.
- The current display-dependent Vision test must be made deterministic or
  explicitly separated as an integration/display test rather than blocking the
  deterministic unit suite.

### Audit and observability

- Persistent audit logging performs synchronous file append work in the tool
  execution path.
- Audit compaction can cause latency spikes.
- The current streaming console activity view cannot distinguish a healthy slow
  operation from a stalled one.
- The running MCP does not expose enough identity/queue/operation data to prove
  which build is answering or why a request is waiting.

### Runtime ownership and rescue

- Direct `tunnel-client run --profile nmbv-windows-commander-final` can recreate
  a second raw owner beside the already-managed runtime.
- A blind two-second restart loop can repeatedly relaunch a deterministic
  failure.
- Windows Commander can currently be asked to terminate components that may be
  essential to its own control path without an explicit self-protection rule.
- There is no independent semantic code-inspection capability available when
  Serena itself is unavailable.

## Architecture

Rescue v2 contains four required units and one optional unit.

### 1. Core MCP server

The existing .NET Windows Commander remains the authoritative process for:

- Windows process control
- services and registry
- filesystem CRUD
- shell/PowerShell/native process execution
- window management
- keyboard/mouse automation
- UI Automation
- screen capture/OCR
- audit/history
- Serena/runtime rescue operations

The core server must not depend on Serena or CodeIntel in order to initialize.

### 2. Bounded operation scheduler

The server accepts multiple in-flight requests but routes them through a small
scheduler rather than unrestricted `Task.Run`.

Default global concurrency: **6**.

Configurable range: **1..10**, but values above the default are opt-in and must
not alter safety semantics.

Execution classes:

| Class | Default limit | Rules |
|---|---:|---|
| Read/inspect | 4 | May run concurrently when operations do not mutate shared state. |
| Process/search | 2 | Long-running work returns operation handles when appropriate. |
| Mutation | 2 global + keyed resource lock | Conflicting canonical paths serialize. |
| Desktop/UI | 1 | Exclusive STA lane for focus, input, UIA, capture/OCR when required. |
| Rescue | reserved access | Health/status and recovery probes must remain serviceable during ordinary load. |

The global cap applies in addition to per-class limits.

No caller is promised exactly six simultaneous operations; the scheduler may
serialize operations based on resource conflicts or execution class.

### 3. Durable operation supervisor

Long-running operations use a Windows Commander-owned operation record with:

- operation id
- parent request id when available
- tool name
- state
- PID when applicable
- queued/start/end timestamps
- elapsed milliseconds
- deadline
- output cursor
- bounded stdout/stderr ring buffers
- cancellation token
- failure fingerprint
- retry/recovery count
- current recovery strategy
- target/resource keys

Required states:

- `QUEUED`
- `RUNNING`
- `WAITING_RESOURCE`
- `WAITING_EXTERNAL`
- `SLOW`
- `SUSPECTED_STALL`
- `RECOVERING`
- `SUCCEEDED`
- `FAILED`
- `CANCELLED`

The supervisor is not an autonomous agent. State transitions are deterministic
and based on observable evidence.

A long operation must expose status/output/cancel capabilities without requiring
the original MCP request to remain open until completion.

Where current tunnel/client support permits MCP Tasks, the public transport
should use the standardized task model. Where compatibility requires otherwise,
the internal operation model remains the single source of truth and a small
compatibility tool surface may expose it.

There must not be two independent task engines.

### 4. Live Rescue terminal observer

A single terminal observer presents structured supervisor state.

The observer **must not render through the MCP server stdout stream**. Stdio
stdout is the JSON-RPC transport and must contain protocol messages only. The
observer therefore runs out-of-process, or consumes a dedicated side channel
such as a bounded local event file/named pipe produced by the supervisor.
Observer failure, terminal rendering failure, or ANSI incompatibility must have
zero effect on MCP request execution.

The initial implementation should use **one** .NET rendering approach,
preferably Spectre.Console in the observer process. Do not add Terminal.Gui,
OpenTelemetry, Aspire, and Spectre simultaneously. When terminal capabilities
are inadequate, the observer falls back to plain line-oriented rendering.

When output is redirected or terminal capabilities are inadequate, fall back to
plain line-oriented structured logging.

Required live fields:

- Windows Commander runtime health
- tunnel health
- Serena health
- optional CodeIntel health
- active / maximum concurrency
- queued operation count
- worker/lane
- operation/tool name
- target summary
- state
- elapsed milliseconds
- deadline/budget
- last progress age
- failure/recovery message

Color contract:

- green: success/healthy
- cyan: running
- blue: queued
- yellow: slow/waiting/warning
- magenta: recovery/strategy change
- red: failure/timeout/blocked
- gray: completed history
- default foreground: ordinary information

The terminal view is observational. It does not contain a second scheduler or
decision engine.

### 5. Isolated Rescue CodeIntel sidecar (optional at runtime, required capability)

CodeIntel is loaded only when semantic source repair is required. It is optional
at runtime so the core rescue MCP can boot without it, but the packaged Rescue v2
release must include and validate the capability.

It must run outside the core MCP process and may be restarted or disabled
without affecting Windows Commander.

Initial Rescue CodeIntel scope is deliberately small:

- find symbol
- find declaration
- find references
- file diagnostics
- symbol diagnostics
- single-file symbol-body replacement/insertion using the language server only
  to resolve the target, followed by the core atomic/CAS filesystem contract;
- safe-delete **preflight** that proves references are absent but does not
  perform an unjournaled cross-file mutation.

Cross-file semantic rename is not part of the initial rescue scope. It may be
added only after an all-or-none multi-file mutation journal, crash recovery,
version preconditions, and rollback tests exist.

The implementation may use SolidLSP or direct language-server adapters, but the
core contract is language-server semantics rather than Serena process reuse.

Not in the initial Rescue CodeIntel scope:

- Serena memories
- NMBV completion ledgers
- NMBV governance
- NMBV task/checkpoint routing
- neural agent loops
- broad autonomous refactoring
- unrestricted REPL execution

If CodeIntel is absent or crashes, core Windows Commander remains usable through
filesystem/search/process/Git/build/test operations.

## Deterministic recovery controller

Recovery uses a finite policy, not an LLM.

Required rules include:

- identical failure fingerprint + identical strategy twice -> prohibit another
  identical retry until evidence changes;
- process alive + observable output/CPU progress -> classify slow, not stalled;
- no progress across the configured observation window -> run one bounded
  diagnostic probe before declaring a stall;
- operation deadline + confirmed stall -> cancel the owned process tree;
- Serena unavailable -> never require Serena for Serena recovery;
- optional CodeIntel unavailable -> degrade semantic repair only;
- file changed after inspection -> reject guarded mutation and require re-read;
- repair validation worsens diagnostics or build state -> rollback the repair;
- three materially different recovery strategies fail -> circuit-break that
  subsystem and surface the evidence rather than looping.

Automatic recovery must never silently broaden into unrelated repository work.

## Filesystem mutation contract

### Atomic writes

For overwrite operations:

1. canonicalize target;
2. acquire the target resource lock;
3. verify optional expected file identity;
4. write a same-filesystem temporary file;
5. flush/close;
6. replace/move atomically where Windows semantics permit;
7. re-read metadata/hash when verification was requested;
8. release lock.

### Stale-read protection

Mutating APIs may accept an optional expected identity containing one or more of:

- SHA-256
- file length
- last-write timestamp

If supplied and no longer true, no mutation occurs.

### Reparse points

Recursive filesystem operations do not traverse reparse points by default.
Following them requires an explicit request and remains subject to canonical
root boundaries.

### Destructive operations

Directory replacement/move must not destroy an existing destination before the
new state is staged sufficiently to permit rollback.

Recycle must either be genuinely implemented using Windows semantics or removed
from the advertised schema. The contract may not advertise an unimplemented
action.

## Process execution contract

- Always preserve and return the real launched PID.
- Maintain bounded stdout and stderr while the process runs.
- Return partial output when a timeout/cancellation occurs.
- Cancellation kills the owned process tree unless the caller explicitly chose
  a detached process.
- Process termination operations verify the requested terminal condition before
  returning `Completed=true`.
- Long-running output is read through cursors rather than returned as an
  unbounded string.

A PowerShell RunspacePool is **not** a Rescue v2 requirement. It may be
considered only after measurements show process startup materially affects the
target workload.

## Search contract

Initial fast-search design:

- use ripgrep when available for content search;
- use the current native implementation as compatibility fallback;
- do not require Everything in Rescue v2;
- return bounded pages/cursors;
- expose whether the fast path or fallback produced the result;
- semantic meaning of a search must not change merely because the accelerator
  is available.

## Self-protection

Windows Commander must know the current control-path identities sufficiently to
guard against accidental self-destruction.

Protected-by-default targets include:

- current Windows Commander MCP process
- its active tunnel-client owner
- direct required parent/supervisor process when known

A destructive request targeting a protected component returns a clear blocked
result unless an explicit narrow override is provided by the caller.

The override must be audited.

This protection does not prevent the managed supervisor from intentionally
replacing an old runtime during a controlled deployment.

## Serena rescue surface

Windows Commander should expose a small deterministic Serena rescue surface,
implemented entirely through Windows/process/filesystem/tunnel knowledge.

Required capabilities:

- runtime status
- process-tree inspection
- router/master health
- tunnel ownership/status
- recent Serena logs
- relevant runtime/config identity
- bounded restart/reconcile action
- post-recovery functional MCP probe

The health result must distinguish:

- process exists
- port/listener exists
- health endpoint passes
- MCP capability probe passes

A healthy PID alone is never sufficient proof of Serena recovery.

## Runtime ownership

There is exactly one authoritative startup path: the managed tunnel runtime.

The supported launcher:

1. reads `runtime.json`;
2. checks managed runtime status;
3. verifies alias, tunnel id, target executable path, and readiness;
4. removes only verified conflicting raw owners;
5. reconnects only if the managed runtime is absent, unhealthy, or points to the
   wrong target;
6. performs a functional MCP probe before reporting success.

The launcher must not contain an infinite raw `run --profile` loop.

Repeated deterministic startup failure uses bounded backoff/circuit breaking and
terminates with evidence.

## Tool metadata and truthful contracts

Tool definition metadata becomes the single source for:

- name
- description
- schema
- execution class
- risk
- confirmation requirement
- default timeout
- result/output policy

Risk classification must not be independently duplicated in a separate static
list where it can drift from the tool definition.

Known contract mismatches in the baseline must be corrected, including:

- `list_windows` hierarchy wording;
- encoding detection claim;
- file-security-summary claim;
- recycle support.

## Protocol direction

Do not add more protocol surface to the current hand-written loop without
tests.

Preferred implementation path:

1. add protocol-conformance tests around current behavior;
2. evaluate the official C# MCP SDK against the current tunnel/client;
3. migrate the transport boundary only if compatibility is proven;
4. keep Windows operation services independent from the transport framework.

A protocol migration must not be bundled with unrelated service rewrites unless
tests prove the boundary first.

## Second-pass adversarial hardening requirements

These requirements close failure modes discovered during the post-spec red-team
pass. They are mandatory because they protect speed, accuracy, and quality at
the same time rather than trading one for another.

### Stdio purity, framing, and response serialization

- MCP stdout is protocol-only. Diagnostics, TUI rendering, progress text, and
  debug logging must never write to stdout.
- Concurrent request execution still uses exactly one serialized response
  writer so JSON-RPC frames cannot interleave.
- Incoming newline-delimited request frames have a configurable hard byte
  limit before JSON deserialization. Oversized requests are rejected without
  materializing unbounded strings.
- The response queue is bounded. A slow or disconnected reader may apply
  backpressure but may not create unbounded memory growth.
- EOF/client disconnect cancels or detaches in-flight operations according to
  their declared lifetime and releases all transport-owned resources.

### Monotonic timing and truthful stall detection

Elapsed time, deadlines, queue wait time, and stall windows use
`Stopwatch`/monotonic time. UTC wall-clock time is used only for human/audit
timestamps. System clock adjustments must not make an operation appear to run
backward, exceed a false deadline, or escape a deadline.

A fixed sleep is not considered evidence of progress. In particular, the
current fixed 150 ms capture-settle delay must be benchmarked against a
compositor-aware wait such as `DwmFlush` or another bounded condition-based
mechanism. The faster mechanism is selected only if capture correctness remains
equal or better.

### Replay, idempotency, and duplicate delivery

Mutating operations support an optional caller operation/idempotency key.
A duplicate key with identical normalized arguments returns the already-known
result/status rather than performing the mutation twice. Reuse of the same key
with different normalized arguments is rejected.

This protects against reconnect/retry ambiguity without making every read
operation stateful.

### Process containment and identity

Owned non-detached process trees should use Windows Job Objects where compatible
so cancellation and server teardown can reliably contain descendants. Job
Object integration must detect an already-jobbed process and fall back safely
rather than assuming assignment always succeeds.

Process identity is not PID alone. Any self-protection, ownership, or destructive
process decision that could be affected by PID reuse uses available identity
evidence such as PID + creation time + executable path. Stale identities fail
closed.

The current `ProcessService.ListProcesses` implementation must also close a
handle-lifetime defect: process objects filtered out before `ToSummary` are not
disposed. Enumeration must dispose every `Process` instance regardless of
filter outcome and isolate races with processes that exit during inspection.

### Window identity and Win32 return-value correctness

HWNDs are reusable. Cached or destructive UI/window actions must revalidate the
window and relevant owning-process identity immediately before action rather
than assuming a previously observed HWND still identifies the same object.

The current `SetWindowState` implementation must not treat the return value of
`ShowWindow` as operation success. The Win32 contract reports whether the
window **was previously visible**, not whether the requested state transition
succeeded. Completion must be verified from observable window state.

### Hierarchical resource locking

Path locking cannot be exact-string-only. Conflicts include ancestor/descendant
relationships after canonicalization, for example:

- `D:\a`
- `D:\a\b.txt`

Lock acquisition therefore uses a deterministic canonical hierarchy and a
single global ordering rule. Multi-resource operations acquire all required
locks in sorted canonical order, never upgrade a held lock, and support
cancellation while waiting. This prevents both parent/child races and lock-order
deadlocks.

### Filesystem TOCTOU and metadata preservation

A guarded write revalidates target identity **after acquiring the resource
lock** and again immediately before the replace/commit boundary when reparse
state could have changed.

Atomic replacement of an existing file must define and test preservation
semantics for:

- ACL/security descriptor where applicable;
- timestamps that should or should not change;
- file attributes;
- encoding/BOM and newline convention for text-preserving edit paths.

No symbolic repair may silently normalize encoding or line endings.

### Search semantic parity

The ripgrep accelerator and native fallback must implement the same documented
search semantics for:

- hidden files;
- ignored files;
- binary files;
- case sensitivity;
- glob inclusion/exclusion;
- encoding limitations;
- maximum results.

If exact parity cannot be provided for an input, the response explicitly states
the engine limitation instead of silently returning a different meaning.

### Audit durability policy

Audit failure behavior is risk-based:

- read-only inspection may continue with an explicit `audit_degraded=true`
  state;
- a high-risk mutation must not silently claim durable completion when its
  required audit record cannot be persisted;
- any override that permits a high-risk mutation during audit degradation must
  be explicit, narrow, surfaced in the result, and itself recorded when
  persistence becomes available.

The persisted audit is always redacted. Raw secret-bearing command/content
arguments are not recoverable from the durable audit file. The public history
contract must not imply that sensitive arguments survive restart.

### Queue admission, fairness, and overload

The scheduler has a bounded admission queue and a defined overload response.
A flood of ordinary work cannot consume the reserved rescue capacity forever,
and rescue traffic cannot starve all normal work indefinitely. Fairness is
measured and tested rather than implemented as unlimited priority.

Queue wait time is included separately from execution time in every operation
record.

### Interactive-session and privilege awareness

Desktop/UI operations report and validate the Windows session they are targeting.
Before keyboard/mouse/UIA/capture operations, the server can distinguish at
least:

- interactive desktop available;
- workstation/session unavailable or locked where detectable;
- current process session id;
- current elevation/integrity capability relevant to the requested operation.

The server must fail fast with evidence instead of hanging against an unavailable
interactive desktop.

### Explicit unattended policy

Unattended behavior is a declared runtime policy, not an accidental property of
whichever CMD happened to launch the current managed runtime.

Runtime status exposes the effective confirmation policy. The managed launcher
ensures the configured policy reaches the actual MCP child. Self-protection,
CAS, bounded execution, and rollback rules remain active in unattended mode.

### Singleton defense in depth

Managed runtime ownership remains the primary singleton mechanism, but the MCP
process additionally supports a production instance key/mutex so an accidental
second raw owner for the same configured instance fails quickly with a clear
diagnostic instead of creating split-brain service.

Test/staging instances use distinct explicit instance keys.

### Build identity and provenance

The running server exposes a machine-readable build identity containing:

- product version;
- source commit;
- build timestamp if reproducibly available;
- executable SHA-256;
- instance key;
- configuration generation/version.

Do not discard Git commit/build metadata merely to produce a short SemVer
display string. Human version and provenance are separate fields.

Deployment remains side-by-side/versioned. A new build is probed before it
becomes authoritative; failure leaves or restores the last-known-good target.

### CodeIntel isolation and workspace versioning

The optional CodeIntel sidecar communicates only over a local, user-scoped
transport such as inherited stdio or a named pipe with current-user ACLs. It
does not open a broadly reachable TCP listener by default.

Every semantic result is bound to an explicit workspace root and document
version/generation. A semantic mutation is rejected if the underlying document
version/hash changed after symbol resolution.

Unsupported languages report `unsupported`; they do not silently fall back to
regex while claiming symbolic semantics.

## Third-pass accuracy, security-boundary, and Windows-behavior findings

### Child-process environment minimization

The current live Windows Commander child was observed to inherit credential-like
environment variable names from its parent process, including the control-plane
credential reference environment and unrelated API-key variables. The MCP child
does not need those secrets to perform Windows operations.

The managed runtime must therefore launch Windows Commander with a minimal
allowlisted environment containing only operating-system/runtime variables and
explicit Windows Commander configuration needed by the child. Control-plane
credentials required by tunnel-client stay in the tunnel process and are not
copied into the MCP child unless a documented capability requires them.

The allowlist must preserve required Windows runtime behavior such as SystemRoot,
PATH where required, TEMP/TMP, USERPROFILE, and explicit .NET/application
settings, while excluding unrelated credential-bearing variables by default.

### Strict tool input contracts

Tool JSON Schemas must reject unknown properties where compatibility permits and
declare server-enforced ranges for bounded integers such as timeouts, result
counts, repeat counts, dimensions, depths, and payload sizes.

Server-side validation remains authoritative even when the client ignores JSON
Schema. A typo in an argument must not silently become a default behavior when
that could change the target or scope of an operation.

JSON-RPC lifecycle validation must also cover the `jsonrpc` version field,
initialize state, malformed request structure, notification behavior, and the
correct protocol error class rather than mapping all structural errors to an
internal error.

### Clipboard and text-input correctness

Clipboard access and clipboard-backed typing run on the exclusive desktop STA
lane and have bounded waits/cancellation. An STA helper may not call unbounded
`Thread.Join()`.

`type_text` currently advertises `speed_ms` but the implementation pastes the
entire string and ignores that argument. Rescue v2 must either remove the
argument from the contract or implement documented semantics; it may not keep a
parameter that has no effect.

The current fixed 400 ms clipboard-restore delay is a latency tax and a race
heuristic. It must be measured and replaced with a bounded, more deterministic
mechanism where one is demonstrably reliable; otherwise the limitation is
documented and isolated from non-text desktop operations.

Input-sequence execution with `abort_on_error=false` must retain per-step failure
evidence instead of swallowing exceptions and returning only a reduced completed
count.

Horizontal scrolling must use the Windows horizontal-wheel event rather than the
vertical-wheel event with a reversed delta. The current implementation uses
`MOUSEEVENTF_WHEEL` for left/right and therefore does not implement the declared
horizontal action correctly.

### No fabricated measurements or confidence

Unknown system/display/vision values are represented as unknown/null, not as
plausible constants.

Current examples that must be corrected include:

- system integrity level reported as the literal string `Unknown` without an
  explicit unknown-data contract;
- display DPI scale hardcoded to 1.0;
- refresh rate/color depth/adapter information returned as zero/empty defaults;
- OCR line confidence returned as 1.0 even though Windows OCR does not expose
  per-line confidence;
- visual window candidates assigned a hardcoded 0.80 confidence.

Accuracy takes precedence over filling every field. A missing measurement is
better than fabricated certainty.

### Windows service/process handle hygiene

Every disposable `Process` and `ServiceController` created during enumeration is
disposed regardless of filter outcome, early exit, or per-item failure.
Enumeration isolates transient races where a process/service disappears while
being inspected instead of failing the entire listing.

### Registry and clipboard result bounds

Registry reads and clipboard reads are subject to explicit result budgets before
large values are materialized into an MCP response. Registry enumeration has a
maximum result count; large binary/string values report bounded metadata or a
bounded representation instead of forcing full allocation followed by a late
response-size rejection.

### Notification and action completion semantics

Result fields use precise semantics such as `requested`, `started`, `observed`,
or `verified` rather than returning `Delivered=true`/`Completed=true` when the
underlying Windows API only proves that a request was issued.

The current notification implementation disposes its `NotifyIcon` immediately
after requesting a balloon notification and cannot truthfully prove delivery.
Likewise, window/process/shell actions must not claim stronger completion than
the observable evidence supports.

### Avoid process-global Windows setting mutations

Window-focus recovery must not depend on temporarily changing machine/session
global foreground-lock configuration when a process crash could prevent
restoration. Prefer APIs and thread-input coordination whose failure scope stays
inside the Windows Commander process. Any unavoidable global mutation requires
an explicit opt-in and a crash-recovery story.

### Audit redaction precision

Durable audit remains secret-safe, but redaction must not destroy unrelated
diagnostic context merely because an argument name contains a broad substring
such as `key`. Redaction policy is tested with positive secret cases and
negative non-secret cases such as registry key paths and keyboard keys.

### Response-data and observer isolation

The terminal observer consumes a versioned, bounded event schema from a
side-channel source. It never scrapes human-formatted MCP stderr logs as its
source of truth and never shares mutable scheduler state with the MCP process.

Observer restarts, slow rendering, terminal resizing, and log rotation must not
block MCP request execution or cause event production to grow without bound.
## Fourth-pass hot-path, transport, and desktop-integrity findings

### Actual transport-frame budgeting

Result-size enforcement must apply to the final serialized JSON-RPC frame, not
only to the inner tool-result text. The current server serializes many result
objects to JSON text and then serializes that JSON again as an MCP text-content
string, so escaping can make the actual wire frame materially larger than the
pre-check.

Rescue v2 must either:

- use structured content where negotiated and compatible; or
- reserve/measure the final JSON-RPC serialization overhead before enqueueing
  the response.

Image/base64 content is also subject to a final serialized-frame budget.

### Tunnel compatibility gate for out-of-order completion

Concurrent execution is enabled through the real tunnel only after an integration
test proves that tunnel-client and the consuming MCP client correctly correlate
responses by JSON-RPC id when requests complete out of submission order.

If that compatibility test fails, Rescue v2 does **not** fake concurrency by
writing unordered responses into an incompatible transport. Long work instead
returns a quick durable operation/task handle so the stdio protocol path remains
responsive while transport responses stay compatible.

### Cached immutable tool registry

Tool descriptors, schemas, execution class, risk, timeout, and output policy are
constructed once into an immutable registry and reused by `tools/list` and
dispatch. This removes repeated allocation and also prevents schema/risk/dispatch
metadata from drifting across separate tables.

### Read truncation must be explicit

A bounded `read_file` response must include enough metadata to distinguish a
complete read from a prefix. At minimum it reports total size when known, bytes
returned, and `truncated`. Text reads must not end in a silently corrupted
partial multibyte character; truncation is aligned to a valid decoding boundary
or returned as bytes/base64 when that cannot be guaranteed.

### Large copy/delete progress and cancellation

Cancellation between directory entries is insufficient for one multi-gigabyte
file. Large file copy uses chunked/cancellable I/O with bytes-progress reporting.
Recursive delete uses explicit bounded enumeration when cancellation semantics
are promised instead of delegating an entire huge tree to one uninterruptible
`Directory.Delete(..., recursive:true)` call.

### Clipboard race protection

Clipboard-backed typing must not restore an old clipboard snapshot over content
the user or another application copied after Windows Commander began its paste.
Where supported, use the Windows clipboard sequence number (or equivalent
identity evidence) to restore only if the clipboard still matches the
automation-owned generation. A concurrent user clipboard change wins.

### Desktop-side-effect hot path

Courtesy UI/audio indicators are outside the correctness path. They may not add
multi-second startup waits or synchronously block a tool on a WPF dispatcher.
The current overlay creation path can wait up to five seconds for its UI thread
and uses synchronous dispatcher invocation. Rescue v2 prewarms or posts indicator
work asynchronously with a strict bounded budget; on failure it drops the
indicator event rather than delaying the requested operation.

The local confirmation UI uses one managed STA dispatcher. A timed-out
confirmation must close/cancel its own dialog; it may not leave an orphaned
`MessageBox` thread after the MCP request has already returned.

### Poisoned-lane containment for non-cancellable Windows APIs

Some in-process Windows/COM/UI Automation calls cannot be force-cancelled safely.
If one exceeds its hard deadline despite cancellation, the affected desktop/UI
lane is marked `POISONED` and circuit-broken. Rescue v2 does not spawn unlimited
replacement threads around an indefinitely blocked COM call.

Core filesystem/process/status/rescue lanes remain available. Recovery may
restart the MCP process if restoring the poisoned desktop lane is required.

### UI Automation reference identity

An element reference is not trusted solely because its UI Automation runtime id
matches an earlier observation. Cached references carry generation, root HWND,
owning process identity, and expiry. Actions revalidate these before invocation
so runtime-id/HWND reuse cannot redirect an action to a different control.

### Window/text metadata completeness

Window-title retrieval must not silently truncate to a fixed 512-character
buffer when the Win32 API can report the required length. Any intentional
truncation is surfaced in metadata.

### Active-window capture must not silently broaden

If an `active_window` capture cannot resolve an actual foreground window, the
tool must return an explicit error/fallback result. It must not silently capture
the full desktop, which changes both privacy scope and semantic meaning.

### Environment and spawned-child inheritance

After the MCP process itself is launched with a minimal environment, child
process execution inherits only that scrubbed baseline plus caller-provided
explicit overrides. Secret-bearing parent/tunnel variables must not reappear
through an execution-service shortcut.

### Sensitive direct-read policy

Direct environment-variable reads whose names match configured secret classes
(password/token/secret/private credential patterns) are not returned accidentally
as ordinary low-risk inspection. The tool either reports redacted/present state
or requires an explicit sensitive-read authorization path. General shell
execution remains a separately classified high-authority capability.

### Registry raw-value semantics

Registry reads document whether expandable strings are returned raw or expanded,
and provide an explicit way to address the default unnamed value. Enumeration
does not silently change semantics based on value type.

### Control-plane startup configuration schema

`runtime.json` and the generated tunnel profile have a versioned schema.
Startup rejects malformed/unknown critical fields, missing target binaries,
unexpected tunnel ids, and configuration generations that do not match the
intended release. Secrets remain references, not serialized plaintext.

### Side-by-side deployment and rollback proof

A deployment never overwrites/renames the currently executing published folder.
It publishes a new immutable version directory, verifies its manifest/hash,
starts/probes it under managed ownership, then changes authority. Failed probes
leave the last-known-good build runnable and produce one bounded rollback path.
## Test strategy

### Deterministic unit/contract suite

Must cover:

- protocol version negotiation and initialize-state enforcement
- malformed/invalid JSON-RPC request classification
- request error isolation
- stdout protocol purity and single response-writer serialization
- request-frame byte limit and bounded response queue
- disconnect/EOF cleanup
- strict unknown-argument rejection and numeric range validation
- response-size limits
- scheduler global limits, admission bounds, fairness, and overload
- exclusive desktop lane
- hierarchical keyed mutation locking and deterministic lock ordering
- monotonic deadline/stall timing under wall-clock change
- idempotent duplicate mutation replay
- real PID preservation and PID-reuse identity rejection
- process/service object disposal under filters and transient exits
- Job Object process-tree containment plus safe fallback
- timeout partial output
- cancellation/process-tree termination
- process termination verification
- `ShowWindow` result semantics and verified window-state completion
- HWND reuse/revalidation
- bounded file reads
- atomic writes and metadata/encoding/newline preservation
- stale-read/CAS rejection including post-lock revalidation
- recursive cancellation
- reparse-point policy
- recycle contract
- search pagination and accelerator/fallback semantic parity
- native search fallback
- clipboard STA timeout/cancellation and bounded reads
- `type_text` contract consistency
- horizontal-wheel correctness
- per-step input-sequence error evidence
- UI tree bounds
- UI cache eviction
- image/OCR dimension limits
- unknown display/system values represented without fabricated certainty
- OCR/visual confidence truthfulness
- registry result bounds
- notification/action evidence-level semantics
- audit persistence, degradation policy, and precise redaction
- MCP child environment allowlist/no control-plane secret inheritance
- unattended-policy propagation
- self-protection
- recovery retry budget
- recovery circuit breaker
- runtime owner reconciliation and production instance mutex
- wrong-target runtime detection
- build provenance/source-SHA reporting
- observer side-channel isolation and bounded event production
- final serialized JSON-RPC frame budgeting including escaping/base64 overhead
- tunnel-client out-of-order response correlation compatibility
- immutable cached tool-registry/schema consistency
- bounded read `truncated`/total-size semantics and multibyte boundary handling
- large-file copy progress/cancellation and recursive-delete cancellation
- clipboard sequence/race-safe restore
- nonblocking indicator hot path and timed-out confirmation cleanup
- poisoned desktop-lane containment for non-cancellable APIs
- UIA generation/root/process reference revalidation
- active-window capture failure without full-desktop scope broadening
- scrubbed environment propagation to spawned child processes
- sensitive direct environment-read policy
- registry raw/default-value semantics
- versioned runtime/profile configuration validation
- side-by-side deployment manifest/hash and last-known-good rollback

### Integration tests

Must cover where the execution environment permits:

- live Windows process lifecycle
- live UI Automation
- display capture/OCR
- tunnel-client managed runtime
- Serena healthy probe
- Serena unavailable -> Windows Commander still usable
- CodeIntel unavailable -> Windows Commander still usable
- CodeIntel diagnostics and one safe semantic edit fixture

Display-dependent tests must be explicitly categorized. A headless environment
must not turn an environment prerequisite into a false product regression.

## Performance acceptance

Performance work is evidence-driven.

Required benchmarks compare the new build against `71daff5`. Benchmark runs
use at least 5 warm-up iterations followed by at least 30 measured iterations
for short operations, reporting median and p95 rather than one-off best cases.

Required benchmarks include:

- cold initialize
- warm `tools/list`
- small read
- bounded directory listing
- content search fixture
- concurrent slow + fast request
- audit-heavy small operations

Mandatory behavioral targets:

- A slow process operation must not head-of-line block an unrelated fast read or
  status operation for the full duration of the slow request.
- During a synthetic >=1 second slow operation, an unrelated fast status/read
  request completes within the greater of 250 ms or 2x that operation's isolated
  p95 on the same machine.
- Small-read, bounded-listing, and warm `tools/list` hot paths use a
  non-inferiority budget: median may not be slower than the `71daff5` baseline
  by more than the greater of 2 ms or 5%, and p95 may not be slower by more
  than the greater of 10 ms or 10%.
- A result outside that margin blocks release until the regression is removed
  or repeated benchmarking proves the baseline/noise model was invalid. A
  correctness or safety feature is not silently purchased with hot-path latency;
  its checks must be moved off the hot path, cached, amortized, or otherwise
  engineered so the user-visible speed contract remains intact.
- A 10,000-operation lightweight stress run must show no monotonic unbounded
  managed-memory, handle, operation-record, UI-element-cache, or queue growth.
- Dashboard/observer absence and presence must not materially change MCP hot-path
  correctness; observer work is outside the protocol execution path.

No benchmark target justifies weakening validation, output bounds, locking,
audit policy, or failure isolation. A speed optimization that changes semantics
or hides evidence is rejected even if it benchmarks faster.

## Severity rubric

The defect register uses these release-blocking definitions:

- **P0:** data loss/corruption, uncontrolled destructive action, security boundary
  failure, or loss of the rescue control plane.
- **P1:** false success on a material operation, wrong-target action, repeatable
  deadlock/livelock/crash loop, or inability to recover a supported primary
  failure mode.
- **P2:** reproducible correctness defect, resource leak/growth, contract
  mismatch, cancellation failure, or performance regression beyond this spec's
  accepted thresholds.
- **P3/P4:** lower-impact usability, diagnostics, or cosmetic defects that do
  not invalidate the defined rescue contract.

Every discovered P0/P1/P2 item must have an owner, reproduction, fix or scope
decision, and regression evidence before release.
## Definition of Done

Rescue v2 is complete only when:

1. Every confirmed problem in this spec is either fixed and covered by evidence,
   or explicitly removed from scope by a reviewed spec amendment.
2. Deterministic tests are green.
3. Required integration tests pass on an environment that satisfies their
   prerequisites.
4. No known P0/P1/P2 defect remains in the defined rescue scope.
5. Fault-injection scenarios demonstrate that failure of Serena, CodeIntel,
   search acceleration, audit persistence, or one tool request does not take
   down the core rescue MCP.
6. The final live managed runtime uses one verified owner and the intended
   versioned executable.
7. A final runtime probe proves health and actual MCP functionality.
8. Performance regression checks meet the behavioral targets above.
9. All residual known lower-severity risks are documented.
10. Source is committed, pushed, reviewed, merged, and remote main is verified
    before the release is called delivered.

## Out of scope / explicitly rejected

To prevent Rescue v2 from becoming another overbuilt control plane:

- no second LLM agent inside Windows Commander;
- no complete Serena/NMBV clone;
- no NMBV memory/governance/checkpoint system duplication;
- no Aspire dashboard;
- no mandatory OpenTelemetry stack;
- no Everything dependency;
- no PowerShell RunspacePool without benchmark evidence;
- no ten unrestricted generic workers;
- no unrestricted REPL;
- no unjournaled cross-file semantic rename in the initial CodeIntel scope;
- no automatic broad repository refactoring;
- no unbounded STA thread joins, unbounded request frames, or unbounded observer queues;
- no fabricated confidence/metrics values when the platform does not provide them;
- no claim that all possible future defects have been eliminated.

## Implementation sequencing

The implementation plan should be split into independently reviewable waves:

1. Correctness and contract defects already present in `71daff5`.
2. Bounded concurrency and durable operation supervision.
3. Transactional filesystem and cancellation hardening.
4. Live terminal observability and deterministic stall/recovery state.
5. Managed runtime/single-owner/self-protection hardening.
6. Minimal isolated Rescue CodeIntel.
7. Fault injection, performance comparison, release validation, deployment.

Each wave must remain usable and testable on its own. Optional CodeIntel work
must not delay delivery of the core rescue/control reliability improvements.
