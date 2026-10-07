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
   the terminal UI, or Serena integration must not terminate the core MCP server.
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

### 4. Live Rescue terminal view

A single terminal UI presents structured supervisor state.

The initial implementation should use **one** .NET terminal rendering approach,
preferably Spectre.Console for a non-interactive live dashboard. Do not add
Terminal.Gui, OpenTelemetry, Aspire, and Spectre simultaneously.

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

### 5. Optional isolated Rescue CodeIntel sidecar

CodeIntel is loaded only when semantic source repair is required.

It must run outside the core MCP process and may be restarted or disabled
without affecting Windows Commander.

Initial Rescue CodeIntel scope is deliberately small:

- find symbol
- find declaration
- find references
- file diagnostics
- symbol diagnostics
- symbol-body replacement or workspace edit
- rename symbol
- safe-delete preflight

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

## Test strategy

### Deterministic unit/contract suite

Must cover:

- protocol version negotiation
- request error isolation
- response-size limits
- scheduler global limits
- exclusive desktop lane
- keyed mutation locking
- real PID preservation
- timeout partial output
- cancellation/process-tree termination
- process termination verification
- bounded file reads
- atomic writes
- stale-read/CAS rejection
- recursive cancellation
- reparse-point policy
- recycle contract
- search pagination
- native search fallback
- UI tree bounds
- UI cache eviction
- image/OCR dimension limits
- audit persistence/redaction
- self-protection
- recovery retry budget
- recovery circuit breaker
- runtime owner reconciliation
- wrong-target runtime detection

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

Required benchmarks compare the new build against `71daff5` for:

- cold initialize
- warm `tools/list`
- small read
- bounded directory listing
- content search fixture
- concurrent slow + fast request
- audit-heavy small operations

Mandatory behavioral target:

A slow process operation must not head-of-line block an unrelated fast read or
status operation for the full duration of the slow request.

No benchmark target justifies weakening validation, output bounds, locking, or
failure isolation.

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
- no automatic broad repository refactoring;
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
