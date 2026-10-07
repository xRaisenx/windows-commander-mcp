# NMBV managed runtime launcher

This directory contains the NMBV-specific launcher used to keep one authoritative tunnel-client owner for the Windows Commander alias.

## Rule

Use `Start-NMBV-WindowsCommander.ps1`. Do not run `tunnel-client run --profile nmbv-windows-commander` in parallel.

The launcher:

1. checks the managed runtime state;
2. accepts readiness only when `process_running`, `healthy`, `ready`, and the configured tunnel ID all match;
3. terminates only matching ad-hoc raw-profile tunnel-client owners for the same executable and alias;
4. removes stale managed runtime state;
5. reconnects through `tunnel-client runtimes connect`;
6. waits for managed readiness or exits with an error.

Copy `runtime.example.json` to `runtime.json` on the target machine and fill in the local paths and tunnel ID. Keep credentials referenced through environment/file references rather than storing raw keys.
