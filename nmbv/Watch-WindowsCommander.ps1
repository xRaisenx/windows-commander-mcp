[CmdletBinding()]
param(
  [string]$EventLogPath = (Join-Path $env:LOCALAPPDATA 'WindowsCommander\activity.jsonl'),
  [ValidateRange(100,5000)]
  [int]$RefreshMs = 250,
  [ValidateRange(8,60)]
  [int]$MaxRows = 18,
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

function Read-Events {
  if(-not (Test-Path -LiteralPath $EventLogPath)){ return @() }

  $take=[Math]::Max(100,$MaxRows*8)
  return @(Get-Content -LiteralPath $EventLogPath -Tail $take |
    ForEach-Object {
      try { $_ | ConvertFrom-Json } catch { $null }
    } |
    Where-Object { $null -ne $_ })
}

function Get-LatestOperations($Events) {
  if($Events.Count -eq 0){ return @() }

  $indexed=@()
  $legacy=0
  foreach($event in $Events) {
    $id=[string]$event.ActivityId
    if([string]::IsNullOrWhiteSpace($id) -or $id -eq '0') {
      $legacy++
      $id="legacy-$legacy"
    }

    $indexed += [pscustomobject]@{
      Key=$id
      Event=$event
    }
  }

  $latest=@($indexed |
    Group-Object Key |
    ForEach-Object { $_.Group[-1].Event } |
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
  $detailWidth=[Math]::Max(24,$width-83)

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

  Write-Host (' Event log: {0}' -f (Clip $EventLogPath ($width-13))) -ForegroundColor DarkGray
  Write-Rule $width '-' 'DarkGray'

  if($lastMutation) {
    Write-Host ' LAST MUTATION ' -ForegroundColor Black -BackgroundColor Magenta -NoNewline
    Write-Host (' {0}  {1}  {2} ms  {3}' -f
      (Get-StateBadge ([string]$lastMutation.State)),
      [string]$lastMutation.Operation,
      [int64]$lastMutation.ElapsedMs,
      (Clip ([string]$lastMutation.Detail) ($width-48))
    ) -ForegroundColor (Get-StateColor ([string]$lastMutation.State))
  } else {
    Write-Host ' LAST MUTATION  none in current window' -ForegroundColor DarkGray
  }

  if($lastFailure) {
    Write-Host ' LAST FAILURE  ' -ForegroundColor Black -BackgroundColor Red -NoNewline
    Write-Host (' {0}  {1}  {2}' -f
      [string]$lastFailure.Operation,
      ([int64]$lastFailure.ElapsedMs),
      (Clip ([string]$lastFailure.Detail) ($width-38))
    ) -ForegroundColor Red
  } else {
    Write-Host ' LAST FAILURE   none' -ForegroundColor Green
  }

  Write-Rule $width '-' 'DarkGray'
  Write-Host (' {0,-6} {1,-13} {2,-9} {3,-9} {4,9}  {5,-25} {6}' -f
    'ID','TIME','STATE','LANE','ELAPSED','ACTION','TARGET / RESULT') -ForegroundColor White
  Write-Rule $width '-' 'DarkGray'

  if($operations.Count -eq 0) {
    Write-Host ''
    Write-Host ' Waiting for Windows Commander activity...' -ForegroundColor DarkGray
  } else {
    foreach($event in $operations) {
      $time=try { ([DateTimeOffset]$event.Timestamp).ToLocalTime().ToString('HH:mm:ss.fff') } catch { '--:--:--.---' }
      $id=if($event.ActivityId){'#'+[string]$event.ActivityId}else{'-'}
      $state=[string]$event.State
      $lane=[string]$event.Lane
      $badge=Get-StateBadge $state
      $elapsed=('{0} ms' -f [int64]$event.ElapsedMs)
      $operation=Clip ([string]$event.Operation) 25
      $detail=Clip ([string]$event.Detail) $detailWidth

      Write-Host (' {0,-6} {1,-13} ' -f $id,$time) -ForegroundColor DarkGray -NoNewline
      Write-Host ('{0,-9} ' -f $badge) -ForegroundColor (Get-StateColor $state) -NoNewline
      Write-Host ('{0,-9} ' -f $lane) -ForegroundColor (Get-LaneColor $lane) -NoNewline
      Write-Host ('{0,9}  ' -f $elapsed) -ForegroundColor White -NoNewline
      Write-Host ('{0,-25} ' -f $operation) -ForegroundColor White -NoNewline
      Write-Host $detail -ForegroundColor (Get-StateColor $state)
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
