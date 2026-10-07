# Tunnel timeout resilience design

Date: 2026-10-07

## Problem

Under tunnel-client, Windows Commander can accept a command that does not produce a response before the control-plane response deadline. The tunnel then drops the response. Re-delivery can repeat the same stalled work. If the MCP stdio child exits or its pipe closes, tunnel-client shuts down and later reports `context deadline exceeded` while stopping. The current in-memory audit log is lost when the MCP process restarts, removing the evidence needed to identify the triggering tool.

Observed production sequence:

1. command forwarded to the stdio MCP server;
2. no usable response for roughly two minutes;
3. tunnel-client logs `command response deadline reached; dropping without posting a response`;
4. retry/re-delivery may repeat;
5. stdio write fails with `file already closed`;
6. tunnel-client shuts down;
7. shutdown cleanup may end with `context deadline exceeded`.

`WINDOWS_COMMANDER_UNATTENDED=1` is not the root cause. In unattended mode the automatic high-risk confirmation gate is bypassed.

## Goals

1. A single tool call must stop waiting and return an MCP tool error before the tunnel response deadline.
2. Cancellation of a waiting process must terminate the spawned process tree.
3. Large synchronous filesystem scans must observe cancellation and must have bounded result counts.
4. Oversized MCP responses must fail locally with an actionable error instead of attempting an unbounded stdio/control-plane response.
5. Audit history must survive MCP process restarts without persisting secrets.
6. A timeout, cancellation, malformed tool request, or oversized result must not terminate the stdio server.
7. Existing safety classification and attended/unattended behavior must remain intact.

## Non-goals

- No Serena/NMBV governance integration.
- No change to risk classifications.
- No removal of high-risk confirmation in attended mode.
- No tunnel-client source changes.
- No background job framework.
- No API/schema breaking change to existing required parameters.

## Design

### Tool-call deadline

`ToolDispatcher` owns a hard call budget. Default: 90 seconds. The value is configurable through `WINDOWS_COMMANDER_TOOL_TIMEOUT_MS` and clamped to a safe range that remains below the observed tunnel deadline.

Dispatch runs on a worker task. The dispatcher uses a linked cancellation token and a bounded wait. If the budget expires it cancels the operation, records `timeout`, observes any eventual background failure, and returns an MCP `isError=true` result. The stdio server remains available for the next request.

### Process cancellation

`ExecutionService` kills the child process tree for both its explicit `timeout_ms` timeout and an external cancellation from the dispatcher. External cancellation is then rethrown so the dispatcher reports the correct timeout/cancel state.

### Filesystem bounds

Recursive directory listing gains optional `max_results` with a bounded default. Directory enumeration and file search receive the dispatcher cancellation token and check it while walking files. Content search checks cancellation while scanning lines. Recursive copy checks cancellation between files/directories.

### Response-size bound

Before a tool result is returned, serialized text and image payloads are measured. The default response budget is configurable through `WINDOWS_COMMANDER_MAX_RESPONSE_BYTES`. Oversized results return an MCP tool error that tells the caller to narrow `max_results`, `max_bytes`, or `max_dimension`.

### Durable audit

Add a JSONL-backed audit implementation. It:

- keeps the existing bounded in-memory queue for current-process raw argument access;
- writes only redacted arguments to disk;
- defaults to `%LOCALAPPDATA%\\WindowsCommander\\audit.jsonl`;
- supports `WINDOWS_COMMANDER_AUDIT_LOG` override;
- rotates the file at a bounded size;
- reads persisted redacted entries for normal `get_operation_history` calls;
- never persists raw secret-bearing arguments.

After restart, redacted history remains available. Sensitive/raw history remains intentionally process-local.

## Acceptance criteria

- A deliberately slow tool call returns a timeout error before the configured dispatcher deadline and the server answers a subsequent request.
- External cancellation kills a spawned child process tree.
- Recursive listing/search stops on cancellation.
- `list_directory` is bounded by `max_results`.
- Oversized result returns `isError=true` instead of emitting the oversized payload.
- Audit entries are readable after constructing a new audit-log instance against the same file.
- Persisted audit arguments are redacted.
- Existing risk-policy, filesystem, MCP protocol, UTF-8, and safety tests remain green.
- Release build and full test suite pass on Windows .NET 10.
