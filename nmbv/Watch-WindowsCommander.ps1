[CmdletBinding()]
param(
  [string]$EventLogPath = (Join-Path $env:LOCALAPPDATA 'WindowsCommander\activity.jsonl'),
  [ValidateRange(100,5000)]
  [int]$RefreshMs = 250,
  [ValidateRange(10,100)]
  [int]$MaxRows = 35
)

$ErrorActionPreference = 'SilentlyContinue'

function Get-StateColor([string]$State) {
  switch ($State) {
    'SUCCESS'         { 'Green' }
    'HEALTHY'         { 'Green' }
    'RUNNING'         { 'Cyan' }
    'QUEUED'          { 'Blue' }
    'SLOW'            { 'Yellow' }
    'WAITING'         { 'Yellow' }
    'SUSPECTED_STALL' { 'Yellow' }
    'RECOVERING'      { 'Magenta' }
    'FAILED'          { 'Red' }
    'TIMEOUT'         { 'Red' }
    'BLOCKED'         { 'Red' }
    'CANCELLED'       { 'DarkGray' }
    default           { 'Gray' }
  }
}

while ($true) {
  $events = @()
  if (Test-Path -LiteralPath $EventLogPath) {
    $events = Get-Content -LiteralPath $EventLogPath -Tail $MaxRows |
      ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $null }
      } |
      Where-Object { $null -ne $_ }
  }

  Clear-Host
  Write-Host ' NMBV WINDOWS COMMANDER RESCUE ' -ForegroundColor Black -BackgroundColor Cyan
  Write-Host (' {0:yyyy-MM-dd HH:mm:ss.fff}   Activity: {1}' -f (Get-Date), $EventLogPath) -ForegroundColor DarkGray
  Write-Host ''
  Write-Host (' {0,-13} {1,-10} {2,-9} {3,9}  {4,-30} {5}' -f 'TIME','STATE','LANE','ELAPSED','OPERATION','DETAIL') -ForegroundColor White
  Write-Host (' ' + ('-' * 112)) -ForegroundColor DarkGray

  foreach ($event in $events) {
    $time = try { ([DateTimeOffset]$event.Timestamp).ToLocalTime().ToString('HH:mm:ss.fff') } catch { '--:--:--.---' }
    $elapsed = ('{0} ms' -f [int64]$event.ElapsedMs)
    $detail = [string]$event.Detail
    if ($detail.Length -gt 48) { $detail = $detail.Substring(0,45) + '...' }
    $operation = [string]$event.Operation
    if ($operation.Length -gt 30) { $operation = $operation.Substring(0,27) + '...' }

    $line = ' {0,-13} {1,-10} {2,-9} {3,9}  {4,-30} {5}' -f $time,$event.State,$event.Lane,$elapsed,$operation,$detail
    Write-Host $line -ForegroundColor (Get-StateColor ([string]$event.State))
  }

  if ($events.Count -eq 0) {
    Write-Host ' Waiting for Windows Commander activity...' -ForegroundColor DarkGray
  } else {
    $last = $events[-1]
    Write-Host ''
    Write-Host (' Active {0}   Queued {1}   Last {2} / {3} ms' -f $last.Active,$last.Queued,$last.State,$last.ElapsedMs) -ForegroundColor White
  }

  Start-Sleep -Milliseconds $RefreshMs
}
