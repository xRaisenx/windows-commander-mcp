[CmdletBinding()]
param(
  [string]$EventLogPath = (Join-Path $env:LOCALAPPDATA 'WindowsCommander\activity.jsonl'),
  [ValidateRange(100,5000)]
  [int]$RefreshMs = 250,
  [ValidateRange(6,40)]
  [int]$MaxRows = 12,
  [switch]$Once
)

$ErrorActionPreference = 'SilentlyContinue'
$Host.UI.RawUI.WindowTitle = 'Windows Commander Rescue - Live Activity'

function Get-StateColor([string]$State) {
  switch ($State) {
    'SUCCESS'         { 'Green' }
    'HEALTHY'         { 'Green' }
    'RUNNING'         { 'Cyan' }
    'QUEUED'          { 'Yellow' }
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

function Get-LaneColor([string]$Lane) {
  switch ($Lane) {
    'READ'    { 'DarkCyan' }
    'PROCESS' { 'Yellow' }
    'MUTATE'  { 'Magenta' }
    'DESKTOP' { 'Blue' }
    default   { 'Gray' }
  }
}

function Get-StateBadge([string]$State) {
  switch ($State) {
    'SUCCESS'         { '[OK]' }
    'RUNNING'         { '[RUN]' }
    'QUEUED'          { '[WAIT]' }
    'SLOW'            { '[SLOW]' }
    'WAITING'         { '[WAIT]' }
    'SUSPECTED_STALL' { '[STALL?]' }
    'RECOVERING'      { '[RECOVER]' }
    'FAILED'          { '[FAIL]' }
    'TIMEOUT'         { '[TIMEOUT]' }
    'BLOCKED'         { '[BLOCK]' }
    'CANCELLED'       { '[CANCEL]' }
    default           { "[$State]" }
  }
}

function Clip([string]$Value,[int]$Max) {
  if([string]::IsNullOrWhiteSpace($Value)){ return '' }
  $clean=($Value -replace '[\r\n\t]+',' ').Trim()
  if($clean.Length -le $Max){ return $clean }
  return $clean.Substring(0,[Math]::Max(1,$Max-3))+'...'
}

function Write-Rule([int]$Width,[char]$Char='-',[ConsoleColor]$Color='DarkGray') {
  Write-Host (' '+($Char.ToString()*[Math]::Max(20,$Width-2))) -ForegroundColor $Color
}

function Write-Detail([string]$Label,[string]$Value,[int]$Width,[ConsoleColor]$Color='Gray') {
  if([string]::IsNullOrWhiteSpace($Value)){ return }
  $prefix="   $Label "
  $body=($Value -replace '[\r\n\t]+',' ').Trim()
  $available=[Math]::Max(24,$Width-$prefix.Length-2)
  $first=$true
  while($body.Length -gt 0) {
    $take=[Math]::Min($available,$body.Length)
    if($take -lt $body.Length) {
      $space=$body.LastIndexOf(' ',$take-1,$take)
      if($space -gt [Math]::Floor($available*0.55)){ $take=$space }
    }
    $part=$body.Substring(0,$take).Trim()
    if($first) {
      Write-Host $prefix -ForegroundColor DarkGray -NoNewline
      Write-Host $part -ForegroundColor $Color
      $first=$false
    } else {
      Write-Host (' ' * $prefix.Length) -NoNewline
      Write-Host $part -ForegroundColor $Color
    }
    $body=$body.Substring($take).TrimStart()
  }
}

function Read-Events {
  if(-not (Test-Path -LiteralPath $EventLogPath)){ return @() }

  $take=[Math]::Max(100,$MaxRows*10)
  $events=@(Get-Content -LiteralPath $EventLogPath -Tail $take |
    ForEach-Object {
      try { $_ | ConvertFrom-Json } catch { $null }
    } |
    Where-Object { $null -ne $_ })

  # Once the new event format is present, hide legacy rows that had no
  # activity id or request/result detail. The dashboard should show useful
  # information, not historical noise.
  if(@($events | Where-Object { $_.ActivityId }).Count -gt 0) {
    $events=@($events | Where-Object { $_.ActivityId })
  }
  return $events
}

function Get-LatestOperations($Events) {
  if($Events.Count -eq 0){ return @() }

  $latest=@($Events |
    Group-Object ActivityId |
    ForEach-Object { $_.Group[-1] } |
    Sort-Object { try { [DateTimeOffset]$_.Timestamp } catch { [DateTimeOffset]::MinValue } })

  if($latest.Count -gt $MaxRows) {
    return @($latest | Select-Object -Last $MaxRows)
  }
  return $latest
}

while($true) {
  $events=Read-Events
  $operations=Get-LatestOperations $events
  $lastEvent=if($events.Count -gt 0){$events[-1]}else{$null}

  $active=if($lastEvent -and $null -ne $lastEvent.Active){[int]$lastEvent.Active}else{@($operations | Where-Object { $_.State -eq 'RUNNING' }).Count}
  $queued=if($lastEvent -and $null -ne $lastEvent.Queued){[int]$lastEvent.Queued}else{@($operations | Where-Object { $_.State -eq 'QUEUED' }).Count}
  $ok=@($operations | Where-Object { $_.State -eq 'SUCCESS' }).Count
  $failed=@($operations | Where-Object { $_.State -in @('FAILED','TIMEOUT','BLOCKED') }).Count
  $mutations=@($operations | Where-Object { $_.Lane -eq 'MUTATE' })
  $lastMutation=if($mutations.Count -gt 0){$mutations[-1]}else{$null}
  $failures=@($operations | Where-Object { $_.State -in @('FAILED','TIMEOUT','BLOCKED') })
  $lastFailure=if($failures.Count -gt 0){$failures[-1]}else{$null}

  $width=120
  try {
    $width=[Math]::Max(100,[Math]::Min(180,$Host.UI.RawUI.WindowSize.Width))
  } catch {}

  Clear-Host
  Write-Host ''
  Write-Host ' WINDOWS COMMANDER RESCUE ' -ForegroundColor Black -BackgroundColor Cyan -NoNewline
  Write-Host '  LIVE ACTIVITY DASHBOARD' -ForegroundColor White
  Write-Rule $width '=' 'DarkCyan'

  Write-Host (' {0:yyyy-MM-dd HH:mm:ss.fff}' -f (Get-Date)) -ForegroundColor DarkGray -NoNewline
  Write-Host '   ACTIVE ' -ForegroundColor Gray -NoNewline
  Write-Host $active -ForegroundColor Cyan -NoNewline
  Write-Host '   QUEUED ' -ForegroundColor Gray -NoNewline
  Write-Host $queued -ForegroundColor Yellow -NoNewline
  Write-Host '   SUCCESS ' -ForegroundColor Gray -NoNewline
  Write-Host $ok -ForegroundColor Green -NoNewline
  Write-Host '   FAILED ' -ForegroundColor Gray -NoNewline
  Write-Host $failed -ForegroundColor $(if($failed -gt 0){'Red'}else{'Green'})

  Write-Host (' Event log: {0}' -f $EventLogPath) -ForegroundColor DarkGray
  Write-Rule $width '-' 'DarkGray'

  Write-Host ' LAST MUTATION' -ForegroundColor Magenta
  if($lastMutation) {
    Write-Host ('   {0}  {1}  {2} ms' -f
      (Get-StateBadge ([string]$lastMutation.State)),
      [string]$lastMutation.Operation,
      [int64]$lastMutation.ElapsedMs
    ) -ForegroundColor (Get-StateColor ([string]$lastMutation.State))
    Write-Detail 'WHAT:' ([string]$lastMutation.Detail) $width (Get-StateColor ([string]$lastMutation.State))
  } else {
    Write-Host '   none in current activity window' -ForegroundColor DarkGray
  }

  Write-Host ' LAST FAILURE' -ForegroundColor Red
  if($lastFailure) {
    Write-Host ('   {0}  {1}  {2} ms' -f
      (Get-StateBadge ([string]$lastFailure.State)),
      [string]$lastFailure.Operation,
      [int64]$lastFailure.ElapsedMs
    ) -ForegroundColor Red
    Write-Detail 'WHY:' ([string]$lastFailure.Detail) $width Red
  } else {
    Write-Host '   none' -ForegroundColor Green
  }

  Write-Rule $width '-' 'DarkGray'
  Write-Host (' {0,-7} {1,-13} {2,-10} {3,-9} {4,9}  {5}' -f
    'ID','TIME','STATE','LANE','ELAPSED','ACTION') -ForegroundColor White
  Write-Rule $width '-' 'DarkGray'

  if($operations.Count -eq 0) {
    Write-Host ''
    Write-Host ' Waiting for Windows Commander activity...' -ForegroundColor DarkGray
  } else {
    foreach($event in $operations) {
      $time=try { ([DateTimeOffset]$event.Timestamp).ToLocalTime().ToString('HH:mm:ss.fff') } catch { '--:--:--.---' }
      $id='#'+[string]$event.ActivityId
      $state=[string]$event.State
      $lane=[string]$event.Lane
      $badge=Get-StateBadge $state
      $elapsed=('{0} ms' -f [int64]$event.ElapsedMs)
      $operation=[string]$event.Operation
      $detail=[string]$event.Detail

      Write-Host (' {0,-7} {1,-13} ' -f $id,$time) -ForegroundColor DarkGray -NoNewline
      Write-Host ('{0,-10} ' -f $badge) -ForegroundColor (Get-StateColor $state) -NoNewline
      Write-Host ('{0,-9} ' -f $lane) -ForegroundColor (Get-LaneColor $lane) -NoNewline
      Write-Host ('{0,9}  ' -f $elapsed) -ForegroundColor White -NoNewline
      Write-Host $operation -ForegroundColor White
      Write-Detail 'WHAT:' $detail $width (Get-StateColor $state)
    }
  }

  Write-Rule $width '=' 'DarkCyan'
  if($lastEvent) {
    Write-Host (' Latest: {0} / {1} / {2} ms   Refresh: {3} ms' -f
      $lastEvent.State,$lastEvent.Operation,$lastEvent.ElapsedMs,$RefreshMs) -ForegroundColor Gray
  } else {
    Write-Host (' Refresh: {0} ms' -f $RefreshMs) -ForegroundColor Gray
  }

  if($Once){ break }
  Start-Sleep -Milliseconds $RefreshMs
}
