# NMBV Windows Commander Rescue v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a faster, more accurate, independently bootable Windows Commander rescue/control plane with bounded concurrency, durable operation supervision, transactional filesystem safety, truthful Windows/UI semantics, a non-blocking terminal observer, deterministic Serena recovery, and isolated minimal CodeIntel.

**Architecture:** Keep the existing .NET Windows Commander as the only authoritative MCP control process. Harden its protocol boundary, route work through a bounded scheduler and durable operation supervisor, isolate desktop/UI work on one STA lane, push observability to a separate named-pipe observer, and keep semantic CodeIntel in a restartable sidecar so Serena or CodeIntel failure cannot take down core rescue. Retain the current hand-written stdio transport for Rescue v2 and harden it behind conformance tests; do not bundle an MCP SDK migration into the same reliability release.

**Tech Stack:** .NET 10 / `net10.0-windows10.0.19041.0`, C# 13, Win32/COM/UI Automation, xUnit 2.5.3, PowerShell, `System.Threading.Channels`, Windows Job Objects, Windows named pipes, ripgrep optional fast path, Spectre.Console 0.57.2 for the observer, and a separate .NET CodeIntel sidecar that speaks standard LSP directly to pinned Roslyn, TypeScript, and Pyright language servers.

**Spec:** `docs/superpowers/specs/2026-10-07-windows-commander-rescue-v2-design.md`

## Global Constraints

- Baseline source is `71daff5af3962134c800d1bf5406a45e34e8279c`; current design/plan branch is documentation-only until implementation begins in an isolated worktree.
- Core Windows Commander must initialize and remain useful when Serena, NMBV Bot, CodeIntel, the observer, search acceleration, or one tool subsystem is unavailable.
- Default global concurrency is 6, configurable from 1..10; read/inspect default 4, process/search default 2, mutation default 2 plus hierarchical resource locks, desktop/UI exactly 1, and rescue retains reserved admission capacity.
- Stdio stdout is protocol-only. Human logs, dashboard rendering, progress, diagnostics, and debug output never write to MCP stdout.
- Default request frame budget: 4 MiB; absolute configurable maximum: 16 MiB. Default MCP text-result budget remains 512 KiB unless the spec is amended.
- Default bounded text file read when `max_bytes` is omitted: 256 KiB; callers may explicitly request more within the final response-frame budget.
- All elapsed/deadline/stall measurements use monotonic time; UTC is for audit/human timestamps only.
- Mutations use canonicalized hierarchical locking, stale-read/CAS checks where supplied, atomic staging where Windows permits, and idempotency keys for replay-sensitive actions.
- No process/window/UI action claims stronger completion than directly observed evidence supports.
- No fabricated DPI, refresh, confidence, integrity, delivery, or completion values. Unknown is represented explicitly.
- The observer is out-of-process and consumes a bounded local side channel; its absence or failure cannot affect MCP execution.
- CodeIntel is optional at runtime but required in the packaged Rescue v2 capability set. It cannot be required for core startup.
- No cross-file semantic rename in initial Rescue v2.
- No PowerShell RunspacePool unless benchmarks later prove process startup is material.
- No Everything dependency, no second LLM agent, no Serena memory/governance duplication, no unrestricted REPL.
- No GitHub Actions.
- Hot-path non-inferiority: median no slower than `max(2 ms, 5%)` versus `71daff5`, p95 no slower than `max(10 ms, 10%)`; regressions block release.
- While a synthetic >=1 s slow operation runs, an unrelated fast read/status must finish within `max(250 ms, 2x isolated p95)`.
- A 10,000-operation lightweight stress run must show no monotonic unbounded growth in managed memory, handles, operation records, UIA cache, or queues.
- Release blocks on every known P0/P1/P2 defect in scope; P3/P4 residual risks are documented.
- Delivery means validate -> commit -> push -> PR/review -> merge -> verify remote main -> publish immutable versioned directory -> managed runtime cutover -> functional probe -> residual-risk report.

## Review Focus

These are the five highest-risk conditions that the implementation must exercise explicitly even though they are easy to miss in ordinary happy-path tests:

1. **Target disappears or changes during a recursive operation:** a file/drive/process/window that vanishes between discovery and action must produce bounded per-item failure, release all locks/handles, and never redirect work to a reused identity. Covered by Tasks 4, 7, 9, and 11.
2. **Client disconnects after work starts:** transport-owned reads cancel; declared durable operations either complete/rollback under the operation supervisor or remain queryable, without orphaned queues/processes. Covered by Tasks 3 and 7.
3. **Interactive desktop becomes unavailable mid-operation:** desktop/UI lane fails explicitly or becomes `POISONED`, while read/process/filesystem/rescue lanes remain usable. Covered by Tasks 5 and 11.
4. **Observer attaches late/restarts repeatedly:** MCP remains unaffected; observer receives current snapshot plus bounded recent events without replaying an unbounded backlog. Covered by Task 12.
5. **Code changes after semantic resolution but before edit:** CodeIntel mutation is rejected by document generation/hash CAS and requires re-resolution. Covered by Task 15.

---

## Wave 1 — Baseline, truthful contracts, and existing correctness defects

### Task 1: Freeze reproducible baseline, benchmark harness, and defect register

**Files:**
- Create: `tests/WindowsCommander.Tests/TestSupport/StdioMcpHarness.cs`
- Create: `tools/Measure-RescueV2.ps1`
- Create: `docs/rescue-v2-defect-register.md`
- Modify: `tests/WindowsCommander.Tests/AutomationServiceTests.cs`
- Create: `tests/WindowsCommander.Tests/DisplayIntegrationTests.cs`

**Interfaces:**
- Produces: `StdioMcpHarness.StartAsync(string executablePath, IReadOnlyDictionary<string,string>? environment = null)` with `SendAsync(...)`, `SendRawAsync(...)`, and `DisposeAsync()`.
- Produces: `tools/Measure-RescueV2.ps1 -Executable <path> -Output <json>` reporting warmups, sample count, median, p95, and raw samples for required benchmark names.
- Produces: a defect register keyed `WC-R2-###` with severity, reproduction, owner task, status, and evidence.

- [ ] **Step 1: Move the display-dependent primary-screen assertion into an integration-classified test and add an explicit environment prerequisite.**

`DisplayIntegrationTests.PrimaryScreenFramesThatScreen` asserts positive bounds only when an interactive display is available; deterministic tests must no longer report a product failure solely because `Screen.PrimaryScreen` is unavailable.

- [ ] **Step 2: Run the deterministic baseline suite.**

Run:
`dotnet test WindowsCommander.slnx -c Release --no-restore --filter "Category!=DisplayIntegration"`

Expected: PASS; record count and duration in `docs/rescue-v2-defect-register.md`.

- [ ] **Step 3: Write `StdioMcpHarness` tests that reproduce current protocol behavior and the known head-of-line blocking baseline.**

Assertions:
- initialize succeeds;
- warm `tools/list` succeeds;
- a >=1 s slow request followed immediately by a fast request demonstrates baseline blocking;
- malformed JSON returns a parse error without process exit.

- [ ] **Step 4: Write the benchmark script and capture the `71daff5` baseline.**

Required benchmark names:
`cold_initialize`, `warm_tools_list`, `small_read`, `bounded_list_directory`, `content_search`, `slow_plus_fast`, `audit_heavy`.

Use at least 5 warmups and 30 measured samples for short operations.

- [ ] **Step 5: Populate the defect register from every confirmed defect in the spec, mapping each to the task that closes it.**

No defect may be marked fixed in this task.

- [ ] **Step 6: Commit.**

`git add tests/WindowsCommander.Tests tools/Measure-RescueV2.ps1 docs/rescue-v2-defect-register.md`
`git commit -m "test: freeze Rescue v2 baseline and defects"`

### Task 2: Replace drifting tool metadata with one immutable catalog and strict schemas

**Files:**
- Create: `src/WindowsCommander.Core/Tools/ToolExecutionClass.cs`
- Create: `src/WindowsCommander.Core/Tools/ToolParameterDefinition.cs`
- Create: `src/WindowsCommander.Core/Tools/ToolDefinition.cs`
- Create: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Modify: `src/WindowsCommander.Safety/Policy/RiskPolicyService.cs`
- Modify: `src/WindowsCommander.Core/Services/Interfaces.cs`
- Test: `tests/WindowsCommander.Tests/ToolCatalogTests.cs`
- Modify: `tests/WindowsCommander.Tests/UnitTest1.cs`

**Interfaces:**
- Produces: `ToolCatalog.All : IReadOnlyList<ToolDefinition>`.
- Produces: `ToolCatalog.GetRequired(string name) : ToolDefinition`.
- `ToolDefinition` contains `Name`, `Description`, `RiskLevel`, `RequiresConfirmation`, `ExecutionClass`, `DefaultTimeoutMs`, `ResultPolicy`, and immutable parameter definitions.
- `ToolDispatcher.ListTools()` renders JSON Schema from the catalog instead of rebuilding ad hoc definitions.
- `RiskPolicyService` classifies through `ToolCatalog`, eliminating its independent high/medium-risk lists.

- [ ] **Step 1: Write failing catalog consistency tests.**

Assert:
- every dispatcher tool exists exactly once in `ToolCatalog`;
- every catalog tool has a dispatch handler;
- risk and confirmation derive from the same definition;
- schemas set `additionalProperties=false`;
- bounded numeric parameters expose minimum/maximum matching server validation;
- no unknown tool defaults to `Low` risk.

- [ ] **Step 2: Add regression tests for truthful contract corrections.**

Assert:
- `list_windows` no longer claims a hierarchy;
- `read_file` no longer claims encoding detection until implemented;
- `get_file_properties` no longer claims an ACL summary unless returned;
- `type_text` schema does not expose ignored `speed_ms`;
- `recycle` remains advertised only if Task 8 implements it.

- [ ] **Step 3: Implement immutable catalog types and migrate `ToolDispatcher`/`RiskPolicyService`.**

Remove `ComputerUseTools` duplication by deriving computer-use/desktop classification from catalog metadata.

- [ ] **Step 4: Enforce server-side unknown-property and numeric-range validation before dispatch.**

Schema is descriptive; runtime validation is authoritative.

- [ ] **Step 5: Run focused and full deterministic tests.**

Run:
`dotnet test WindowsCommander.slnx -c Release --no-restore --filter "FullyQualifiedName~ToolCatalog|FullyQualifiedName~RiskPolicy"`

Then deterministic full suite.

- [ ] **Step 6: Commit.**

`git commit -am "refactor: centralize truthful tool contracts"`

### Task 3: Harden JSON-RPC stdio transport without changing service semantics

**Files:**
- Create: `src/WindowsCommander.McpServer/Mcp/JsonRpcProtocol.cs`
- Create: `src/WindowsCommander.McpServer/Mcp/StdioJsonRpcServer.cs`
- Create: `src/WindowsCommander.McpServer/Mcp/McpRequestHandler.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/McpModels.cs`
- Modify: `src/WindowsCommander.McpServer/Program.cs`
- Test: `tests/WindowsCommander.Tests/McpProtocolTests.cs`
- Test: `tests/WindowsCommander.Tests/TestSupport/StdioMcpHarness.cs`

**Interfaces:**
- `StdioJsonRpcServer.RunAsync(Stream input, Stream output, CancellationToken)`.
- `McpRequestHandler.HandleAsync(JsonRpcRequest request, CancellationToken) : Task<JsonRpcResponse?>`.
- `JsonRpcProtocol.SupportedVersions : IReadOnlySet<string>` contains exactly `2024-11-05` for Rescue v2. A client requesting another version receives `2024-11-05`; the server never echoes an unimplemented version. Adding a later protocol version requires its own conformance tests and reviewed change.
- One bounded response channel and one response writer own stdout.
- Default request-frame maximum is 4 MiB; hard configurable cap 16 MiB.

- [ ] **Step 1: Write failing conformance tests.**

Cover:
- invalid JSON -> `-32700`;
- invalid request structure/jsonrpc value -> `-32600`;
- unknown method -> `-32601`;
- invalid params -> `-32602`;
- notification -> no response;
- tools before initialize -> rejected;
- supported/unsupported protocol negotiation;
- one failing request leaves connection alive;
- no stderr/dashboard text appears on stdout;
- oversized request rejected before unbounded allocation.

- [ ] **Step 2: Write response-framing tests.**

Assert:
- only one writer writes stdout;
- concurrent handler completions cannot interleave JSON;
- final serialized frame, not just inner text, respects the configured budget;
- image/base64 results are budgeted at final-frame size.

- [ ] **Step 3: Implement protocol classes and slim `Program.cs` to composition + `RunAsync`.**

Retain current transport model; do not add the official SDK dependency in Rescue v2.

- [ ] **Step 4: Add EOF/disconnect cleanup tests.**

Transport-owned requests cancel; durable operations declared by later Task 6 are allowed to outlive the transport only through their supervisor policy.

- [ ] **Step 5: Run protocol tests and deterministic full suite.**

- [ ] **Step 6: Commit.**

`git commit -am "feat: harden MCP stdio protocol boundary"`

### Task 4: Correct process/service identity, resource hygiene, and completion evidence

**Files:**
- Create: `src/WindowsCommander.Core/Models/ProcessIdentity.cs`
- Create: `src/WindowsCommander.Windows/Native/JobObject.cs`
- Modify: `src/WindowsCommander.Windows/Native/NativeMethods.cs`
- Modify: `src/WindowsCommander.Windows/Services/ExecutionService.cs`
- Modify: `src/WindowsCommander.Windows/Services/ProcessService.cs`
- Modify: `src/WindowsCommander.Windows/Services/WindowsServiceDiscoveryService.cs`
- Modify: `src/WindowsCommander.Core/Models/ExecutionModels.cs`
- Modify: `src/WindowsCommander.Core/Models/ProcessModels.cs`
- Test: `tests/WindowsCommander.Tests/ExecutionServiceTests.cs`
- Modify: `tests/WindowsCommander.Tests/ExecutionServiceCancellationTests.cs`
- Create: `tests/WindowsCommander.Tests/ProcessServiceTests.cs`

**Interfaces:**
- `ProcessIdentity(int ProcessId, DateTimeOffset? StartTime, string? ExecutablePath)`.
- `CommandExecutionResult` gains real `ProcessId` and preserves partial stdout/stderr on timeout/cancellation.
- `ProcessStartResult.ProcessId` is always the actual launched PID when process creation succeeded.
- `JobObject.TryAssign(Process process) : JobAssignmentResult` provides owned-tree containment with explicit fallback when assignment is impossible.
- `ManageProcess` verifies the requested terminal condition before returning success.

- [ ] **Step 1: Write failing tests for real PID and partial output.**

A command that prints `before`, sleeps past timeout, then prints `after` must return the real PID, timeout=true, and preserve `before` only.

- [ ] **Step 2: Write process-tree containment and cancellation tests.**

Verify child/grandchild marker files never appear after timeout/cancel when Job Object containment is available; fallback behavior remains explicit.

- [ ] **Step 3: Write handle-leak/transient-exit tests for process and service enumeration.**

Repeated filtered enumeration must not monotonically increase process handles; a process that exits mid-inspection must not fail the whole list.

- [ ] **Step 4: Implement `ProcessIdentity`, Job Object support, PID preservation, partial-output capture, and disposal fixes.**

- [ ] **Step 5: Add PID-reuse/self-identity primitives needed by Task 11.**

Destructive process actions accept/verify optional expected identity before kill when supplied.

- [ ] **Step 6: Run focused tests, then deterministic full suite.**

- [ ] **Step 7: Commit.**

`git commit -am "fix: make process execution identity and completion truthful"`

### Task 5: Correct desktop input, window, clipboard, and measurement semantics before concurrency

**Files:**
- Create: `src/WindowsCommander.Windows/Services/StaDesktopExecutor.cs`
- Modify: `src/WindowsCommander.Windows/Native/NativeMethods.cs`
- Modify: `src/WindowsCommander.Windows/Services/InputService.cs`
- Modify: `src/WindowsCommander.Windows/Services/ClipboardService.cs`
- Modify: `src/WindowsCommander.Windows/Services/WindowService.cs`
- Modify: `src/WindowsCommander.Windows/Services/ScreenService.cs`
- Modify: `src/WindowsCommander.Windows/Services/SystemInfoService.cs`
- Modify: `src/WindowsCommander.Windows/Services/VisionService.cs`
- Modify: `src/WindowsCommander.Windows/Services/ControlIndicatorService.cs`
- Modify: `src/WindowsCommander.Core/Models/AutomationModels.cs`
- Modify: `src/WindowsCommander.Core/Models/ScreenModels.cs`
- Modify: `src/WindowsCommander.Core/Models/SystemIntegrationModels.cs`
- Modify: `src/WindowsCommander.Core/Models/SystemModels.cs`
- Test: `tests/WindowsCommander.Tests/DesktopCorrectnessTests.cs`
- Modify: `tests/WindowsCommander.Tests/AutomationServiceTests.cs`

**Interfaces:**
- `IStaDesktopExecutor.InvokeAsync<T>(Func<T> action, TimeSpan timeout, CancellationToken)`.
- `InputSequenceResult` contains total steps, completed steps, and per-step error records when `abort_on_error=false`.
- Confidence fields become nullable where the platform supplies no confidence.
- Completion/result records distinguish `Requested`, `Started`, `Observed`, and `Verified` where applicable.

- [ ] **Step 1: Write failing tests for horizontal wheel, `type_text` contract, per-step failures, and bounded STA waits.**

Left/right must use `MOUSEEVENTF_HWHEEL`; schema/service no longer accept ignored `speed_ms`.

- [ ] **Step 2: Add clipboard sequence-number race test.**

If a simulated external clipboard generation changes after paste ownership, restore must not overwrite the newer clipboard.

- [ ] **Step 3: Add `ShowWindow` semantic test and window-identity revalidation fixture.**

`SetWindowState` may not use the Win32 BOOL as success; it verifies observable resulting state.

- [ ] **Step 4: Add no-fabricated-measurements tests.**

Unknown DPI/refresh/color/adapter/integrity/OCR confidence/visual confidence serialize as unknown/null rather than constants.

- [ ] **Step 5: Add active-window capture scope test.**

No foreground window -> explicit failure; never silently capture the full desktop.

- [ ] **Step 6: Implement one reusable STA executor and migrate clipboard/typing/confirmation/UI side effects to bounded execution.**

Timed-out confirmation closes/cancels its owned dialog; no orphan `MessageBox` thread.

- [ ] **Step 7: Make indicators non-blocking.**

Prewarm or asynchronously post overlay/audio work; tool latency never waits up to the current five-second overlay-thread startup.

- [ ] **Step 8: Remove process-global foreground-lock mutation from normal focus recovery.**

Use per-process/thread coordination only; if a legacy opt-in fallback remains, default it off and test restoration.

- [ ] **Step 9: Run focused desktop tests on an interactive session and deterministic suite headlessly.**

- [ ] **Step 10: Commit.**

`git commit -am "fix: make desktop automation bounded and truthful"`

## Wave 2 — Bounded concurrency and durable operations

### Task 6: Add bounded scheduler, hierarchical resource locks, and exclusive desktop lane

**Files:**
- Create: `src/WindowsCommander.Core/Operations/InvocationContext.cs`
- Create: `src/WindowsCommander.McpServer/Scheduling/RequestScheduler.cs`
- Create: `src/WindowsCommander.McpServer/Scheduling/ResourceLockManager.cs`
- Create: `src/WindowsCommander.McpServer/Scheduling/SchedulerOptions.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/McpRequestHandler.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Test: `tests/WindowsCommander.Tests/RequestSchedulerTests.cs`
- Test: `tests/WindowsCommander.Tests/ResourceLockManagerTests.cs`

**Interfaces:**
- `RequestScheduler.ExecuteAsync(ToolDefinition tool, IReadOnlyList<string> resourceKeys, Func<CancellationToken,Task<object>> work, CancellationToken requestToken)`.
- `ResourceLockManager.AcquireAsync(IReadOnlyList<string> canonicalPaths, CancellationToken) : ValueTask<IAsyncDisposable>`.
- `SchedulerOptions` defaults: global 6, read 4, process 2, mutation 2, desktop 1, rescue reserved 1; configurable global 1..10.
- Admission and response queues are bounded; queue wait time is measured separately.

- [ ] **Step 1: Write concurrency-limit tests.**

Assert global/per-lane maximums are never exceeded and desktop lane is exactly one.

- [ ] **Step 2: Write hierarchical conflict tests.**

`D:\a` conflicts with `D:\a\b.txt`; unrelated roots may proceed concurrently. Multi-resource locks sort canonical keys and never deadlock under reversed caller order.

- [ ] **Step 3: Write fairness/overload tests.**

Flood ordinary work; rescue request must obtain capacity. Flood rescue; normal work must still make bounded progress. Full queue returns a defined overload response rather than unbounded allocation.

- [ ] **Step 4: Implement scheduler and route tool calls by catalog `ExecutionClass`.**

- [ ] **Step 5: Add real stdio tunnel-compatibility integration test for out-of-order responses.**

If tunnel/client correlation passes, enable parallel response completion in final runtime config. If it fails, keep response completion serial and rely on Task 7 durable operation handles for long work; do not weaken the scheduler internally.

- [ ] **Step 6: Run the slow+fast benchmark.**

Required: fast request <= `max(250 ms, 2x isolated p95)` while slow operation runs.

- [ ] **Step 7: Commit.**

`git commit -am "feat: add bounded request scheduling"`

### Task 7: Add durable operation supervisor, progress, output cursors, and anti-loop recovery state

**Files:**
- Create: `src/WindowsCommander.Core/Operations/OperationModels.cs`
- Create: `src/WindowsCommander.Core/Operations/IOperationSupervisor.cs`
- Create: `src/WindowsCommander.McpServer/Operations/OperationSupervisor.cs`
- Create: `src/WindowsCommander.McpServer/Operations/RecoveryPolicy.cs`
- Modify: `src/WindowsCommander.Windows/Services/ExecutionService.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Test: `tests/WindowsCommander.Tests/OperationSupervisorTests.cs`
- Test: `tests/WindowsCommander.Tests/RecoveryPolicyTests.cs`

**Interfaces:**
- `OperationState`: `Queued`, `Running`, `WaitingResource`, `WaitingExternal`, `Slow`, `SuspectedStall`, `Recovering`, `Succeeded`, `Failed`, `Cancelled`.
- `OperationHandle(string OperationId, int? ProcessId, OperationState State)`.
- `OperationSnapshot` includes queued/start/end timestamps, monotonic elapsed ms, deadline, last-progress age, PID, failure fingerprint, strategy id/count, resource keys.
- `OperationOutputPage(string OperationId, long NextCursor, string StandardOutput, string StandardError, bool EndOfStream)`.
- `IOperationSupervisor.Start(...)`, `Get(...)`, `ReadOutput(...)`, `CancelAsync(...)`.
- New tools: `get_operation_status`, `read_operation_output`, `cancel_operation`.
- Per-operation stdout/stderr ring-buffer budget: 1 MiB each; cursor reads are bounded.

- [ ] **Step 1: Write operation lifecycle tests.**

Cover every state, bounded history eviction, output paging, cancellation, and no unbounded growth.

- [ ] **Step 2: Write monotonic-time tests.**

Simulated wall-clock jump must not change elapsed/deadline decisions.

- [ ] **Step 3: Write deterministic recovery tests.**

Same failure fingerprint + same strategy twice blocks identical retry; progress means `Slow`, not stalled; no progress gets one diagnostic probe; three materially distinct failed strategies circuit-break subsystem.

- [ ] **Step 4: Implement supervisor and integrate long `execute_process` / `execute_powershell`.**

`wait_for_exit=false` returns an operation handle; short synchronous waits preserve backward-compatible result shape where possible.

- [ ] **Step 5: Test client disconnect.**

Durable operations remain supervised/queryable; transport-owned non-durable work cancels and releases scheduler capacity.

- [ ] **Step 6: Run stress test for 10,000 lightweight operation records with bounded retention.**

- [ ] **Step 7: Commit.**

`git commit -am "feat: supervise long operations durably"`

## Wave 3 — Transactional filesystem and search

### Task 8: Make reads explicit and writes atomic/CAS/idempotent

**Files:**
- Create: `src/WindowsCommander.Core/Models/FileIdentity.cs`
- Create: `src/WindowsCommander.Windows/Services/AtomicFileWriter.cs`
- Modify: `src/WindowsCommander.Core/Models/FileSystemModels.cs`
- Modify: `src/WindowsCommander.Core/Services/Interfaces.cs`
- Modify: `src/WindowsCommander.Windows/Services/FileSystemService.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Test: `tests/WindowsCommander.Tests/FileMutationTests.cs`
- Modify: `tests/WindowsCommander.Tests/FileSystemServiceTests.cs`

**Interfaces:**
- `FileIdentity(string? Sha256, long? Length, DateTimeOffset? LastWriteTimeUtc)`.
- `FileReadResult` adds `TotalBytes` and `Truncated`.
- `WriteFileAsync(..., FileIdentity? expectedIdentity, string? idempotencyKey, CancellationToken)`.
- Omitted `max_bytes` defaults to 256 KiB.
- Atomic writer stages on the same filesystem, flushes, revalidates CAS after lock acquisition and immediately before commit, then replaces/moves atomically where supported.

- [ ] **Step 1: Write failing bounded-read tests.**

Assert `Truncated`, `BytesRead`, `TotalBytes`, and valid UTF-8 boundary behavior; no partial multibyte corruption.

- [ ] **Step 2: Write atomic-write crash/failure simulation tests.**

Injected failure before commit leaves original file intact and removes temp artifacts.

- [ ] **Step 3: Write CAS tests.**

Change file after read/before write -> mutation rejected. Change reparse/identity after lock -> rejected.

- [ ] **Step 4: Write metadata-preservation tests.**

Text-preserving edits retain BOM/newline convention and required attributes/ACL semantics defined by implementation.

- [ ] **Step 5: Write idempotency replay tests.**

Same key + same normalized args -> same known result; same key + different args -> rejected.

- [ ] **Step 6: Implement and run focused/full tests.**

- [ ] **Step 7: Commit.**

`git commit -am "feat: make file writes atomic and guarded"`

### Task 9: Make copy/move/delete/recycle cancellable, reparse-safe, and rollback-capable

**Files:**
- Create: `src/WindowsCommander.Windows/Services/PathMutationService.cs`
- Create: `src/WindowsCommander.Windows/Native/ShellRecycleBin.cs`
- Modify: `src/WindowsCommander.Windows/Native/NativeMethods.cs`
- Modify: `src/WindowsCommander.Windows/Services/FileSystemService.cs`
- Modify: `src/WindowsCommander.Core/Models/FileSystemModels.cs`
- Test: `tests/WindowsCommander.Tests/PathMutationTests.cs`

**Interfaces:**
- Recursive operations default `followReparsePoints=false`.
- Large file copy uses chunked async I/O and reports bytes progress through `OperationContext`.
- Multi-path replacement/move stages before destructive commit and returns rollback evidence.
- `recycle` either succeeds through Windows recycle semantics or returns an explicit supported failure; it never throws `NotSupportedException` for an advertised action.

- [ ] **Step 1: Write reparse-point tests.**

Default recursion never crosses junction/symlink boundary; explicit follow still enforces canonical root boundary.

- [ ] **Step 2: Write large-file cancellation/progress test.**

Cancel during one large file copy, not merely between files; destination is absent or explicitly partial-staged, never falsely complete.

- [ ] **Step 3: Write recursive-delete cancellation test.**

No uninterruptible `Directory.Delete(... recursive:true)` path remains when cancellable behavior is promised.

- [ ] **Step 4: Write directory-overwrite rollback test.**

Failed move does not delete the previous destination before replacement is safe.

- [ ] **Step 5: Implement real recycle action and tests.**

- [ ] **Step 6: Commit.**

`git commit -am "feat: harden destructive path operations"`

### Task 10: Add bounded search pages with ripgrep acceleration and exact native fallback semantics

**Files:**
- Create: `src/WindowsCommander.Core/Models/SearchModels.cs`
- Create: `src/WindowsCommander.Windows/Services/SearchService.cs`
- Modify: `src/WindowsCommander.Core/Services/Interfaces.cs`
- Modify: `src/WindowsCommander.Windows/Services/FileSystemService.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Test: `tests/WindowsCommander.Tests/SearchServiceTests.cs`
- Create: `tests/WindowsCommander.Tests/Fixtures/SearchFixture/`

**Interfaces:**
- `SearchQuery` explicitly models roots, name pattern, content query, hidden, ignore policy, binary policy, case mode, max results, cursor.
- `SearchPage(IReadOnlyList<FileSearchResult> Results, string? NextCursor, string Engine, bool Truncated, IReadOnlyList<string> Limitations)`.
- `SearchService.SearchAsync(SearchQuery, CancellationToken)`.
- Fast engine is `rg` when compatible with query and found; otherwise `native`.
- Default page 100, hard maximum 1000.

- [ ] **Step 1: Build a fixture covering hidden, ignored, binary, Unicode, case, inaccessible, and disappearing files.**

- [ ] **Step 2: Write parity tests that run the same query through `rg` and native engines.**

If exact parity cannot be guaranteed for an input, `Limitations` must state why and the engine must not silently change semantics.

- [ ] **Step 3: Write cursor/pagination and cancellation tests.**

- [ ] **Step 4: Implement fast path + native fallback.**

Do not add Everything.

- [ ] **Step 5: Run search benchmark against `71daff5`.**

- [ ] **Step 6: Commit.**

`git commit -am "feat: add bounded accelerated search"`

## Wave 4 — Desktop isolation, UIA safety, audit, and observability

### Task 11: Bound UI Automation/vision and poison only the desktop lane on non-cancellable hangs

**Files:**
- Create: `src/WindowsCommander.Windows/Services/UiElementCache.cs`
- Modify: `src/WindowsCommander.Windows/Services/UiAutomationService.cs`
- Modify: `src/WindowsCommander.Windows/Services/VisionService.cs`
- Modify: `src/WindowsCommander.Windows/Services/WindowService.cs`
- Modify: `src/WindowsCommander.McpServer/Scheduling/RequestScheduler.cs`
- Modify: `src/WindowsCommander.Core/Models/AutomationModels.cs`
- Test: `tests/WindowsCommander.Tests/UiAutomationSafetyTests.cs`
- Test: `tests/WindowsCommander.Tests/VisionBoundsTests.cs`

**Interfaces:**
- UI element cache entries carry generation, root HWND, process identity, created/last-used monotonic time, TTL, and bounded capacity.
- `ReadUiTree` adds `max_elements` default 1000/max 5000 and cancellation.
- `FindUiElement` traverses predicate-first and stops when max results reached.
- Screenshot/OCR validates max dimension <=4096 and a hard input pixel budget before bitmap allocation.
- Desktop lane can enter `POISONED`; other scheduler lanes remain healthy.

- [ ] **Step 1: Write UIA cache eviction/generation/reuse tests.**

Stale runtime id/HWND/process identity cannot authorize a later action.

- [ ] **Step 2: Write bounded UI tree tests.**

Traversal stops at element budget; `Truncated=true`; filter-first path does not materialize the entire tree.

- [ ] **Step 3: Write image/OCR allocation tests.**

Oversized requested region/dimension is rejected before allocating full source bitmap.

- [ ] **Step 4: Replace fixed capture sleep where a bounded compositor condition proves equal/better correctness.**

Benchmark current 150 ms delay versus `DwmFlush`/bounded condition. Keep the measured safer option; record result in defect register.

- [ ] **Step 5: Write poisoned-lane containment test.**

Simulated non-cancellable UIA hang -> desktop lane circuit-breaks; `read_file`, process status, and rescue status still succeed.

- [ ] **Step 6: Implement and run focused/full tests.**

- [ ] **Step 7: Commit.**

`git commit -am "feat: isolate and bound desktop automation"`

### Task 12: Make audit durable without hot-path blocking and expose a non-blocking named-pipe observer

**Files:**
- Modify: `src/WindowsCommander.Core/Safety/Interfaces.cs`
- Create: `src/WindowsCommander.Core/Observability/ObserverEvent.cs`
- Create: `src/WindowsCommander.Safety/Audit/AuditWriter.cs`
- Modify: `src/WindowsCommander.Safety/Audit/PersistentAuditLog.cs`
- Create: `src/WindowsCommander.McpServer/Observability/ObserverEventSink.cs`
- Create: `src/WindowsCommander.Observer/WindowsCommander.Observer.csproj`
- Create: `src/WindowsCommander.Observer/Program.cs`
- Modify: `WindowsCommander.slnx`
- Modify: `src/WindowsCommander.McpServer/WindowsCommander.McpServer.csproj`
- Test: `tests/WindowsCommander.Tests/AuditDurabilityTests.cs`
- Test: `tests/WindowsCommander.Tests/ObserverEventSinkTests.cs`

**Interfaces:**
- Audit persistence exposes best-effort vs durable write outcome; high-risk mutation cannot claim durable completion if required audit persistence failed.
- `ObserverEvent` version 1 contains runtime/component health, operation id/lane/tool/target/state, queued ms, elapsed ms, deadline, last-progress age, and failure/recovery summary; no raw secrets.
- `ObserverEventSink.TryPublish(ObserverEvent)` is non-blocking and bounded.
- Named pipe uses current-user-only access and a stable instance-key-derived name.
- Observer project references `Spectre.Console` **0.57.2** only; no Terminal.Gui/OpenTelemetry/Aspire dependency.

- [ ] **Step 1: Write audit redaction tests.**

Secrets redact; non-secret `registry_key`, keyboard `key`, and key-path diagnostics remain useful.

- [ ] **Step 2: Write audit degradation tests.**

Read-only operation may report `audit_degraded`; high-risk durable operation cannot silently succeed without required persistence.

- [ ] **Step 3: Implement persistent writer with bounded queue and off-hot-path compaction.**

No `File.AppendAllText` per operation.

- [ ] **Step 4: Write observer isolation tests.**

No observer, slow observer, reconnecting observer, and resized/restarted observer cannot block MCP or grow producer memory unboundedly.

- [ ] **Step 5: Implement observer UI with required color contract and millisecond timing.**

Green success, cyan running, blue queued, yellow slow/waiting, magenta recovery, red failure, gray completed.

- [ ] **Step 6: Benchmark audit-heavy hot path with observer absent and present.**

Must satisfy non-inferiority budget.

- [ ] **Step 7: Commit.**

`git commit -am "feat: add durable audit and live rescue observer"`

## Wave 5 — Runtime security, ownership, self-protection, and Serena rescue

### Task 13: Harden environment, singleton ownership, build identity, and managed deployment

**Files:**
- Create: `src/WindowsCommander.McpServer/Runtime/ProcessEnvironmentSanitizer.cs`
- Create: `src/WindowsCommander.McpServer/Runtime/InstanceMutex.cs`
- Create: `src/WindowsCommander.McpServer/Runtime/BuildIdentity.cs`
- Create: `src/WindowsCommander.McpServer/Runtime/RuntimeStatusService.cs`
- Modify: `src/WindowsCommander.Windows/Services/EnvironmentService.cs`
- Modify: `src/WindowsCommander.Windows/Services/ExecutionService.cs`
- Modify: `src/WindowsCommander.McpServer/Program.cs`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Create: `nmbv/runtime.schema.json`
- Modify: `nmbv/runtime.example.json`
- Modify: `nmbv/Start-NMBV-WindowsCommander.ps1`
- Create: `nmbv/Publish-NMBV-WindowsCommander.ps1`
- Test: `tests/WindowsCommander.Tests/RuntimeHardeningTests.cs`
- Create: `tests/WindowsCommander.Tests/BuildIdentityTests.cs`

**Interfaces:**
- `ProcessEnvironmentSanitizer.ScrubCurrentProcess()` runs before service construction and preserves only required Windows/runtime variables plus `WINDOWS_COMMANDER_*`; unrelated secret-bearing variables are removed.
- Spawned child processes inherit the scrubbed baseline plus explicit caller overrides only.
- Direct reads of sensitive variable names return redacted presence unless explicit sensitive-read authorization is provided.
- `InstanceMutex.Acquire(instanceKey)` prevents a second production MCP instance with the same key.
- New `server_status` tool returns product version, source commit, executable SHA-256, instance key, configuration generation, effective confirmation policy, queue/operation summary, and subsystem health.
- Runtime config schema is versioned and rejects unknown critical fields/missing target/wrong tunnel id.
- Deployment publishes immutable versioned directories and maintains a last-known-good manifest.

- [ ] **Step 1: Write environment-inheritance tests using fake secret variables.**

MCP status may indicate presence policy but must not expose values; spawned child environment excludes them.

- [ ] **Step 2: Write singleton tests.**

Second same-key process fails fast; explicit test/staging key can coexist.

- [ ] **Step 3: Write build identity/status tests.**

Do not strip source commit from provenance even when human SemVer remains `0.1.1`/release-tag style.

- [ ] **Step 4: Add runtime JSON schema validation tests for malformed/wrong-target configs.**

- [ ] **Step 5: Harden launcher.**

One managed owner only; raw owner cleanup is verified and narrow; readiness checks alias+tunnel+target+health+functional MCP probe. Remove any documented/raw infinite restart loop.

- [ ] **Step 6: Implement side-by-side publish/cutover/rollback script.**

Never rename/overwrite the running publish directory.

- [ ] **Step 7: Run live managed-runtime tests in staging instance key before touching the authoritative alias.**

- [ ] **Step 8: Commit.**

`git commit -am "feat: harden runtime ownership and provenance"`

### Task 14: Add deterministic Serena rescue tools without depending on Serena internals

**Files:**
- Create: `src/WindowsCommander.Core/Models/RescueModels.cs`
- Create: `src/WindowsCommander.McpServer/Rescue/SerenaRescueOptions.cs`
- Create: `src/WindowsCommander.McpServer/Rescue/SerenaRescueService.cs`
- Create: `src/WindowsCommander.McpServer/Rescue/SerenaRecoveryPolicy.cs`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Modify: `nmbv/runtime.example.json`
- Test: `tests/WindowsCommander.Tests/SerenaRescueServiceTests.cs`
- Create: `tests/WindowsCommander.Tests/Fixtures/FakeTunnelClient/`
- Create: `tests/WindowsCommander.Tests/Fixtures/FakeSerena/`

**Interfaces:**
- Tools: `serena_status`, `serena_recent_logs`, `serena_reconcile`.
- `SerenaHealth` separately reports process evidence, router/listener evidence, HTTP health evidence, MCP functional-probe evidence, source/runtime identity, and ownership conflicts.
- `SerenaRecoveryPolicy` is a finite state machine; it never invokes Serena to repair Serena.
- Recovery strategies have fingerprints/attempt counts and obey the Task 7 anti-loop policy.

- [ ] **Step 1: Write status tests for process-only, port-only, HTTP-only, and fully functional MCP states.**

Healthy PID alone is not `healthy`.

- [ ] **Step 2: Write ownership-conflict and wrong-runtime-identity tests.**

- [ ] **Step 3: Write anti-loop recovery tests.**

Repeated identical recovery strategy twice -> blocked until evidence changes; three distinct failed strategies -> circuit-break.

- [ ] **Step 4: Implement reconcile using configured external launch/status commands and Windows/tunnel evidence only.**

No Serena memory/checkpoint/governance dependency.

- [ ] **Step 5: Fault-injection integration: make fake Serena unavailable while proving Windows Commander `read_file`, process status, and recovery status continue to work.**

- [ ] **Step 6: Commit.**

`git commit -am "feat: add deterministic Serena rescue controls"`

## Wave 6 — Isolated minimal CodeIntel

### Task 15: Build and package an independent .NET LSP rescue sidecar

**Files:**
- Create: `src/WindowsCommander.CodeIntel/WindowsCommander.CodeIntel.csproj`
- Create: `src/WindowsCommander.CodeIntel/Program.cs`
- Create: `src/WindowsCommander.CodeIntel/Protocol/SidecarProtocol.cs`
- Create: `src/WindowsCommander.CodeIntel/Lsp/LspClient.cs`
- Create: `src/WindowsCommander.CodeIntel/Lsp/LspProcess.cs`
- Create: `src/WindowsCommander.CodeIntel/Lsp/LanguageServerManifest.cs`
- Create: `src/WindowsCommander.CodeIntel/Services/CodeIntelService.cs`
- Create: `codeintel/language-servers.json`
- Create: `codeintel/package.json`
- Create: `codeintel/package-lock.json`
- Create: `tools/Build-CodeIntel.ps1`
- Create: `src/WindowsCommander.McpServer/CodeIntel/CodeIntelClient.cs`
- Create: `src/WindowsCommander.Core/Models/CodeIntelModels.cs`
- Modify: `WindowsCommander.slnx`
- Modify: `src/WindowsCommander.Core/Tools/ToolCatalog.cs`
- Modify: `src/WindowsCommander.McpServer/Mcp/ToolDispatcher.cs`
- Test: `tests/WindowsCommander.Tests/CodeIntelClientTests.cs`
- Test: `tests/WindowsCommander.Tests/LspProtocolTests.cs`
- Create: `tests/WindowsCommander.Tests/Fixtures/CodeIntelFixture/`

**Interfaces:**
- The sidecar is a separate .NET process and contains no Serena application code and no SolidLSP runtime dependency; it implements only the standard LSP requests required by Rescue v2.
- Pinned language-server manifest for Windows x64:
  - C#: Roslyn Language Server `5.5.0-2.26078.4`, NuGet package `roslyn-language-server.win-x64`, SHA-256 `7F3D4119E75305399E6FAA81A68240B33C48B94AD523A904594ABD00DB95572A`.
  - TypeScript/JavaScript: `typescript@5.9.3` + `typescript-language-server@5.1.3`.
  - Python: `pyright@1.1.403`.
- `Build-CodeIntel.ps1` verifies the Roslyn package hash and performs `npm ci` from the committed lockfile for TypeScript/Pyright dependencies. Node absence degrades TypeScript/Python CodeIntel only; C# and core Windows Commander stay usable.
- Sidecar transport is inherited stdio or a current-user-only named pipe; no broadly reachable TCP listener.
- Requests: `find_symbol`, `declaration`, `references`, `diagnostics_file`, `diagnostics_symbol`, `replace_symbol_single_file`, `insert_symbol_single_file`, `safe_delete_preflight`.
- Every response carries workspace root, canonical document path, SHA-256/document generation, language id, language-server identity/version, and whether diagnostics are complete.
- Unsupported language or unavailable pinned server returns `unsupported` / `unavailable`; no regex masquerades as semantics.
- Symbol mutation returns an edit proposal tied to document identity; Task 8's core atomic/CAS writer performs the actual file mutation.
- `LspClient` supports only initialize/shutdown, textDocument didOpen/didClose, documentSymbol, definition, references, and publishDiagnostics required by the tool surface. No general LSP proxy is exposed.

- [ ] **Step 1: Add the sidecar project and failing protocol-framing tests.**

Assert Content-Length framing, request-id correlation, bounded response size, child-server timeout, malformed LSP response isolation, and clean shutdown.

- [ ] **Step 2: Commit the pinned dependency manifest and lockfile, then make `Build-CodeIntel.ps1` verify/install exactly those versions.**

The build fails on Roslyn hash mismatch, npm lock drift, missing package, or unexpected executable path.

- [ ] **Step 3: Write C# fixture tests against pinned Roslyn.**

Assert symbol lookup, declaration, references, file diagnostics, symbol diagnostics, and safe-delete preflight for the Windows Commander-style fixture.

- [ ] **Step 4: Write TypeScript/JavaScript fixture tests against TypeScript LS 5.1.3 + TypeScript 5.9.3.**

Use the same semantic assertions as Step 3 where the protocol capability exists.

- [ ] **Step 5: Write Python fixture tests against Pyright 1.1.403.**

Use the same semantic assertions as Step 3 where the protocol capability exists.

- [ ] **Step 6: Write stale-document mutation tests.**

Resolve symbol, change file, then attempt proposed edit -> Task 8 CAS rejects it and CodeIntel requires re-resolution.

- [ ] **Step 7: Implement lazy sidecar startup and per-language-server lifecycle.**

A language-server crash degrades only that language; repeated identical restart failure obeys Task 7 retry/circuit-break rules.

- [ ] **Step 8: Write core-degradation tests.**

Stop CodeIntel entirely -> core Windows Commander remains healthy. Remove Node from the sidecar environment -> C# remains available while TypeScript/Python report `unavailable`.

- [ ] **Step 9: Run one real C# self-repair fixture end-to-end: resolve symbol -> propose single-file edit -> CAS apply -> diagnostics -> rollback on worsened diagnostics.**

- [ ] **Step 10: Commit.**

`git commit -am "feat: add isolated rescue code intelligence"`

## Wave 7 — Adversarial evaluation, performance, review, and delivery

### Task 16: Run complete defect closure, fault injection, stress, and performance evaluation

**Files:**
- Modify: `docs/rescue-v2-defect-register.md`
- Create: `docs/rescue-v2-evaluation.md`
- Create: `tools/Test-RescueV2Faults.ps1`
- Modify: `tools/Measure-RescueV2.ps1`

**Interfaces:**
- Evaluation document records baseline SHA, candidate SHA, executable SHA-256, test commands/results, benchmark median/p95 comparison, fault scenarios, known residual P3/P4 issues, and final go/no-go.
- No P0/P1/P2 item may be open.

- [ ] **Step 1: Run deterministic full suite from clean Release build.**

Run:
`dotnet build WindowsCommander.slnx -c Release`
`dotnet test WindowsCommander.slnx -c Release --no-build --filter "Category!=DisplayIntegration"`

Expected: PASS with zero deterministic failures/warnings introduced by Rescue v2.

- [ ] **Step 2: Run interactive/display integration tests on a real desktop.**

Include UIA, capture/OCR, clipboard/input, confirmation cleanup, and observer rendering.

- [ ] **Step 3: Run fault-injection matrix.**

At minimum:
- one MCP tool throws;
- slow request;
- client disconnect;
- full scheduler queue;
- audit path unavailable;
- ripgrep absent/crashes;
- CodeIntel absent/crashes;
- Serena absent;
- desktop unavailable/poisoned;
- duplicate mutation replay;
- stale file identity;
- runtime wrong target;
- duplicate raw owner;
- deployment candidate probe fails.

Each failure must demonstrate intended isolation/rollback.

- [ ] **Step 4: Run 10,000-operation stress and handle/memory/cache/queue growth checks.**

- [ ] **Step 5: Run `71daff5` and candidate performance suites on the same machine/session.**

Block on hot-path non-inferiority or slow+fast threshold failure.

- [ ] **Step 6: Re-review every spec requirement against the defect register and candidate evidence.**

Anything uncovered becomes a new defect row before release; do not redefine it away without a reviewed spec amendment.

- [ ] **Step 7: Run Superpowers code-review gate on the complete branch.**

Critical/Important findings are fixed and revalidated; incorrect findings are rejected with evidence.

- [ ] **Step 8: Commit evaluation evidence.**

`git add docs/rescue-v2-defect-register.md docs/rescue-v2-evaluation.md tools`
`git commit -m "test: complete Rescue v2 adversarial evaluation"`

### Task 17: Publish, cut over, verify remote main, and prove live rescue operation

**Files:**
- Modify: `README.md`
- Modify: `nmbv/README.md`
- Create: `docs/releases/2026-10-07-windows-commander-rescue-v2.md`

**Interfaces:**
- Immutable publish directory name includes final source SHA prefix.
- Runtime alias remains the one authoritative managed alias.
- Final live `server_status` proves source commit/executable hash/instance key/config generation.
- Last-known-good version remains available until post-cutover functional validation passes.

- [ ] **Step 1: Rebase/sync against current remote main and rerun deterministic validation if main changed.**

- [ ] **Step 2: Push implementation branch and open PR.**

No GitHub Actions are required; attach local validation/evaluation evidence to PR description.

- [ ] **Step 3: Resolve review comments and rerun affected tests/benchmarks.**

- [ ] **Step 4: Merge only with zero open P0/P1/P2 defect and green required evidence.**

- [ ] **Step 5: Verify remote `main` SHA, then build/publish from that exact merged SHA.**

- [ ] **Step 6: Publish to a new immutable versioned directory and run staging managed-runtime probe.**

- [ ] **Step 7: Cut the authoritative managed alias to the new executable.**

Verify:
- exactly one managed owner;
- no raw conflicting owner;
- `healthy=true`;
- `ready=true`;
- expected tunnel id;
- expected executable path;
- `server_status` source SHA/hash match;
- normal Windows read/process call works;
- long-operation handle works;
- observer can attach;
- Serena status works with Serena healthy;
- core Windows Commander still works when CodeIntel is stopped.

- [ ] **Step 8: Run final rollback drill without destroying the new build.**

Prove last-known-good target can be restored through the bounded deployment path.

- [ ] **Step 9: Update evaluation document to `DELIVERED`, including remote-main SHA and live executable SHA-256.**

- [ ] **Step 10: Commit/push any final documentation-only delivery evidence and verify remote main again.**

## Implementation Order and Review Gates

Execute Tasks 1–17 in order. A task is not complete until its focused test is red first where applicable, the minimal implementation makes it green, deterministic regression tests pass, and the task is committed.

After each wave:
1. run the deterministic suite;
2. review the wave diff against the spec;
3. update the defect register;
4. do not start the next wave with an unresolved P0/P1 caused by the current wave.

Wave 6 CodeIntel may be developed after the core rescue waves are stable, but the final Rescue v2 release is not `DELIVERED` until its required packaged capability and degradation tests pass.

## Execution Method

For this repository and risk profile, prefer **Native execution by ChatGPT in an isolated worktree**, task-by-task, with Superpowers TDD/systematic-debugging controls and a fresh whole-branch review before merge. The tasks have tightly coupled public interfaces across scheduler, operation supervisor, filesystem, and runtime; keeping one coordinator reduces integration drift. Independent review is still mandatory at wave boundaries and before merge.

Implementation must begin from a clean isolated worktree. Do not modify the currently deployed publish directory or the user's live `D:\DEV\nmbv-tools\windows-commander-mcp-71daff5` runtime in place.
