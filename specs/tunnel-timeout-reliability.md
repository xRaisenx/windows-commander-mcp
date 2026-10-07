# Tunnel timeout reliability hardening

## Problem

A Windows Commander stdio session can become unavailable when one MCP tool call outlives the tunnel control-plane response deadline or produces an oversized text response. The tunnel then drops the response, retries may queue behind the still-running request, the stdio child can be lost, and tunnel shutdown may itself report a context deadline. The in-memory-only audit log also loses the operation that caused the failure after the MCP process restarts.

A separate NMBV launcher concern exists outside the upstream MCP binary: one alias must have one runtime owner. Managed `runtimes connect/status` ownership and ad-hoc `run --profile` ownership must not be mixed.

## Requirements

1. Bound every `tools/call` below the tunnel response deadline. Default MCP request budget: 90 seconds. Allow an environment override only inside a safe 5-110 second range.
2. Propagate request cancellation into tool execution. External cancellation of PowerShell/native process waits must kill the child process tree before returning.
3. Return a normal MCP `isError=true` tool result on request timeout; do not terminate the stdio server.
4. Bound serialized text tool results. Default maximum: 512 KiB. Oversized results must return a concise error instructing the caller to narrow the query or use a bounded option.
   - Recursive `list_directory` enumeration is cancellable and defaults to at most 1,000 entries, with an explicit maximum of 5,000.
   - `search_files` checks request cancellation while traversing roots and file content.
5. Persist a redacted bounded audit history under LocalAppData by default so the pre-crash operation remains visible after process restart. Raw sensitive values must never be written to disk.
6. Audit timeout outcomes distinctly from ordinary errors.
7. Preserve unattended semantics: `WINDOWS_COMMANDER_UNATTENDED=1` continues to bypass high-risk confirmation; it is not a timeout control.
8. Preserve existing MCP schemas and behavior except for bounded timeout/output failure behavior.
9. Add regression coverage for durable audit persistence/redaction and cancellation cleanup.
10. Document the launcher rule: exactly one tunnel-client ownership model per alias; NMBV should use managed runtime ownership only.

## Implementation plan

1. Add a server-level request deadline and pass its cancellation token through `ToolDispatcher`.
2. Ensure synchronous process execution kills its child process tree when that request deadline is cancelled.
3. Add a bounded UTF-8 text-result guard before MCP serialization returns a response.
4. Replace the process-lifetime-only audit implementation at server startup with a redacted persistent JSONL audit implementation that reloads recent history.
5. Add focused regression tests for durable redaction/capacity and cancellation cleanup.
6. Add an NMBV managed-runtime launcher that rejects split-brain ownership by terminating only matching ad-hoc raw-profile owners before reconnecting the managed alias.
7. Validate launcher syntax, compile the .NET 10 solution, run focused reliability tests, run the full suite, and compare any unrelated failure against clean `main`.
8. Commit the isolated diff, push it to the user-owned fork, open a PR, merge after validation, publish a self-contained build, deploy it, then verify managed runtime health and post-deployment MCP calls.

## Runtime contract

- `WINDOWS_COMMANDER_REQUEST_TIMEOUT_MS`: optional integer, clamped to 5000..110000, default 90000.
- `WINDOWS_COMMANDER_MAX_TEXT_RESULT_BYTES`: optional integer, clamped to 65536..4194304, default 524288.
- `WINDOWS_COMMANDER_AUDIT_LOG`: optional absolute or relative audit JSONL path. Default: `%LOCALAPPDATA%\WindowsCommander\audit.jsonl`.
- Persistent history stores redacted arguments only and is bounded by the configured audit capacity plus file compaction.
- A caller that needs a command to continue beyond the MCP request budget must start it asynchronously where the tool supports `wait_for_exit=false`, then poll separately.

## NMBV launcher contract

- Do not run `tunnel-client run --profile nmbv-windows-commander` as a parallel owner.
- Start/recover through `tunnel-client runtimes connect/status/stop/rm` only.
- Readiness is valid only when the managed runtime record reports the expected live process identity plus healthy and ready.
- A stale health URL must never be accepted as proof that the managed runtime process is alive.

## Validation

- `dotnet build WindowsCommander.slnx -c Release`
- `dotnet test WindowsCommander.slnx -c Release`
- focused audit persistence test
- focused cancellation test
- diff review for secrets, schema drift, and unrelated changes
