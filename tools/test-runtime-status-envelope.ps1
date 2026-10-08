[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ServerExe,
    [ValidateRange(1,200)][int]$Cycles = 20
)

$ErrorActionPreference = 'Stop'
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $ServerExe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $false
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$psi.Environment['WINDOWS_COMMANDER_INSTANCE_KEY'] = ('status-contract-' + [Guid]::NewGuid().ToString('N'))
$psi.Environment['WINDOWS_COMMANDER_UNATTENDED'] = '1'
$proc = [System.Diagnostics.Process]::Start($psi)
if ($null -eq $proc) { throw 'Cannot start isolated Windows Commander process' }
try {
    for ($i = 1; $i -le $Cycles; $i++) {
        $request = @{
            jsonrpc = '2.0'
            id = $i
            method = 'tools/call'
            params = @{ name = 'get_runtime_status'; arguments = @{} }
        } | ConvertTo-Json -Depth 8 -Compress
        $proc.StandardInput.WriteLine($request)
        $proc.StandardInput.Flush()
        $line = $proc.StandardOutput.ReadLine()
        if ([string]::IsNullOrEmpty($line)) { throw ('No JSON-RPC response in cycle ' + $i) }
        $response = $line | ConvertFrom-Json -Depth 24
        if ($response.id -ne $i -or $response.error) { throw ('Invalid JSON-RPC reply at cycle ' + $i) }
        if ($null -eq $response.result.content -or @($response.result.content).Count -lt 1) {
            throw ('MCP content array missing at cycle ' + $i)
        }
        $item = $response.result.content[0]
        if ($item.type -ne 'text' -or [string]::IsNullOrEmpty($item.text)) {
            throw ('MCP text content missing at cycle ' + $i)
        }
        $status = $item.text | ConvertFrom-Json -Depth 24
        if ($status.healthy -ne $true -or $status.process_id -ne $proc.Id) {
            throw ('Unhealthy or incorrect isolated process identity at cycle ' + $i)
        }
    }
    Write-Output ('PASS cycles=' + $Cycles + ' isolated_pid=' + $proc.Id)
}
finally {
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(3000)) {
        $proc.Kill()
        $proc.WaitForExit()
    }
    $proc.Dispose()
}
