# Tunnel timeout resilience implementation plan

Date: 2026-10-07

1. Add focused regression tests first for durable audit persistence/redaction, filesystem result bounds/cancellation, dispatcher timeout survival, oversized-result rejection, and execution cancellation.
2. Add a persistent JSONL audit implementation in `WindowsCommander.Safety` and wire it in `Program.cs`.
3. Add configurable bounded tool-call and response-size budgets to `ToolDispatcher` while preserving MCP `isError=true` tool-failure semantics.
4. Make filesystem recursive listing/search/copy observe cancellation and bound directory result count.
5. Make `ExecutionService` kill child process trees when cancellation comes from the dispatcher, not only when its own `timeout_ms` expires.
6. Update tool schema/help text for `list_directory.max_results` and document the new environment controls.
7. Build `WindowsCommander.slnx -c Release`, run `dotnet test WindowsCommander.slnx -c Release`, then run the read-only smoke harness from an isolated checkout.
8. Review the final diff for unrelated changes and safety regressions.
9. Push the validated branch, open a PR against `main`, inspect the PR diff/status, and squash-merge only if the validated head SHA is unchanged.
10. Verify the resulting `main` commit and leave deployment of the new binary as a separate explicit step; repository delivery must not silently overwrite the currently running installation.
