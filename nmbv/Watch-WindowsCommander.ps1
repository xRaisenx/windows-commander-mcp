[CmdletBinding()]
param(
  [string]$EventLogPath = (Join-Path $env:LOCALAPPDATA 'WindowsCommander\activity.jsonl'),
  [ValidateRange(100,5000)][int]$RefreshMs = 250,
  [ValidateRange(6,40)][int]$MaxRows = 14,
  [ValidateRange(250,60000)][int]$SlowMs = 1000,
  [ValidateRange(1000,120000)][int]$StallMs = 10000,
  [switch]$Once
)

$ErrorActionPreference='SilentlyContinue'
$Host.UI.RawUI.WindowTitle='Windows Commander Rescue - Live Control Center'

function Clip([string]$Value,[int]$Max){
  if([string]::IsNullOrWhiteSpace($Value)){return ''}
  $v=($Value -replace '[\r\n\t]+',' ').Trim()
  if($v.Length -le $Max){return $v}
  if($Max -le 3){return $v.Substring(0,$Max)}
  return $v.Substring(0,$Max-3)+'...'
}
function Pad([string]$Value,[int]$Width){
  $v=Clip $Value $Width
  if($v.Length -lt $Width){return $v+(' '*($Width-$v.Length))}
  return $v
}
function Line([int]$Width,[char]$Char='-'){return $Char.ToString()*[Math]::Max(1,$Width)}
function C([string]$Text,[ConsoleColor]$Color='Gray',[switch]$N){Write-Host $Text -ForegroundColor $Color -NoNewline:$N}
function Border([int]$Width,[ConsoleColor]$Color='DarkCyan'){Write-Host ('+'+(Line ($Width-2) '-')+'+') -ForegroundColor $Color}
function LaneColor([string]$Lane){
  switch($Lane){'READ'{'Cyan'} 'PROCESS'{'Yellow'} 'MUTATE'{'Magenta'} 'DESKTOP'{'Blue'} default{'Gray'}}
}
function StateColor([string]$State,[long]$Ms){
  if($State -in @('FAILED','TIMEOUT','BLOCKED')){return 'Red'}
  if($State -eq 'SUCCESS'){return 'Green'}
  if($State -eq 'CANCELLED'){return 'DarkGray'}
  if($Ms -ge $StallMs){return 'Red'}
  if($Ms -ge $SlowMs){return 'Yellow'}
  if($State -eq 'RUNNING'){return 'Cyan'}
  if($State -eq 'QUEUED'){return 'Yellow'}
  return 'Gray'
}
function Badge([string]$State,[long]$Ms){
  if($State -eq 'RUNNING' -and $Ms -ge $StallMs){return 'STALL'}
  if($State -eq 'RUNNING' -and $Ms -ge $SlowMs){return 'SLOW'}
  switch($State){
    'SUCCESS'{'OK'} 'RUNNING'{'RUN'} 'QUEUED'{'WAIT'} 'FAILED'{'FAIL'}
    'TIMEOUT'{'TIMEOUT'} 'BLOCKED'{'BLOCK'} 'CANCELLED'{'CANCEL'}
    default{Clip $State 8}
  }
}
function ReadEvents {
  if(-not (Test-Path -LiteralPath $EventLogPath)){return @()}
  $take=[Math]::Max(360,$MaxRows*28)
  return @(
    Get-Content -LiteralPath $EventLogPath -Tail $take |
      ForEach-Object {try{$_|ConvertFrom-Json}catch{$null}} |
      Where-Object {$null -ne $_ -and $_.ActivityId}
  )
}
function LatestOps($Events){
  if($Events.Count -eq 0){return @()}
  $v=@(
    $Events | Group-Object ActivityId | ForEach-Object {$_.Group[-1]} |
      Sort-Object {try{[DateTimeOffset]$_.Timestamp}catch{[DateTimeOffset]::MinValue}}
  )
  if($v.Count -gt $MaxRows){return @($v|Select-Object -Last $MaxRows)}
  return $v
}
function LiveMs($Event,[DateTimeOffset]$Now){
  $ms=[int64]$Event.ElapsedMs
  if($Event.State -in @('RUNNING','QUEUED')){
    try{
      $age=[int64][Math]::Max(0,($Now-[DateTimeOffset]$Event.Timestamp).TotalMilliseconds)
      if($age -gt $ms){$ms=$age}
    }catch{}
  }
  return $ms
}
function Pct($Values,[int]$P){
  if($Values.Count -eq 0){return 0}
  $s=@($Values|Sort-Object)
  $i=[Math]::Ceiling(($P/100.0)*$s.Count)-1
  $i=[Math]::Max(0,[Math]::Min($s.Count-1,[int]$i))
  return [int64]$s[$i]
}
function Header([int]$Width,[DateTimeOffset]$Now){
  Border $Width Cyan
  C '| ' DarkCyan -N
  C 'WINDOWS COMMANDER' White -N
  C ' RESCUE ' Black -N
  C (Pad ' LIVE CONTROL CENTER ' ($Width-29)) White -N
  C '|' DarkCyan
  $sub=' Fast | Reliable | Isolated | Observable'
  $clock=$Now.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss.fff')
  C '| ' DarkCyan -N
  C (Pad $sub ($Width-$clock.Length-5)) DarkGray -N
  C $clock DarkGray -N
  C ' |' DarkCyan
  Border $Width Cyan
}
function Cards([int]$Width,$Active,$Queued,$Success,$Failed,$Median,$P95){
  $labels=@('ACTIVE','QUEUED','SUCCESS','FAILED','MEDIAN','P95')
  $values=@("$Active","$Queued","$Success","$Failed","$Median ms","$P95 ms")
  $colors=@('Cyan','Yellow','Green',$(if($Failed){'Red'}else{'Green'}),'White',$(if($P95 -ge $SlowMs){'Yellow'}else{'Cyan'}))
  $cw=[Math]::Floor((($Width-2)-5)/6)
  C '|' DarkCyan -N
  for($i=0;$i -lt 6;$i++){if($i){C ' ' DarkGray -N};C (Pad (' '+$labels[$i]) $cw) DarkGray -N}
  C '|' DarkCyan
  C '|' DarkCyan -N
  for($i=0;$i -lt 6;$i++){if($i){C ' ' DarkGray -N};C (Pad (' '+$values[$i]) $cw) $colors[$i] -N}
  C '|' DarkCyan
  Border $Width DarkCyan
}
function Section([string]$Name,[int]$Width,[ConsoleColor]$Color='Cyan'){
  $label=" $Name "
  C '+' $Color -N
  C $label $Color -N
  C ((Line ([Math]::Max(0,$Width-2-$label.Length)) '-')+'+') $Color
}
function Activity($Ops,[int]$Width,[DateTimeOffset]$Now){
  Section 'LIVE ACTIVITY' $Width Cyan
  $dw=[Math]::Max(18,$Width-80)
  C '| ' Cyan -N
  C ('{0,-6} {1,-12} {2,-8} {3,-8} {4,10}  {5,-22} {6}' -f 'ID','TIME','STATE','LANE','ELAPSED','ACTION','DETAIL') White -N
  C ((' '*[Math]::Max(0,$dw-6))+' |') Cyan
  C ('|'+(Line ($Width-2) '-')+'|') DarkGray
  if($Ops.Count -eq 0){
    C ('|'+(Pad ' Waiting for Windows Commander activity...' ($Width-2))+'|') DarkGray
  }else{
    foreach($e in $Ops){
      $time=try{([DateTimeOffset]$e.Timestamp).ToLocalTime().ToString('HH:mm:ss.fff')}catch{'--:--:--.---'}
      $ms=LiveMs $e $Now
      $state=[string]$e.State
      $lane=[string]$e.Lane
      C '| ' Cyan -N
      C ('{0,-6} ' -f ('#'+$e.ActivityId)) DarkGray -N
      C ('{0,-12} ' -f $time) DarkGray -N
      C ('{0,-8} ' -f (Badge $state $ms)) (StateColor $state $ms) -N
      C ('{0,-8} ' -f $lane) (LaneColor $lane) -N
      C ('{0,7} ms  ' -f $ms) (StateColor $state $ms) -N
      C ('{0,-22} ' -f (Clip ([string]$e.Operation) 22)) White -N
      C (Pad ([string]$e.Detail) $dw) Gray -N
      C ' |' Cyan
    }
  }
  Border $Width Cyan
}
function Dual($Mutation,$Failure,[int]$Width,[DateTimeOffset]$Now){
  $gap=3;$lw=[Math]::Floor(($Width-$gap)/2);$rw=$Width-$gap-$lw
  C ('+'+(Pad ' LAST MUTATION ' ($lw-2))+'+') Magenta -N
  C (' '*$gap) DarkGray -N
  C ('+'+(Pad ' LAST FAILURE ' ($rw-2))+'+') $(if($Failure){'Red'}else{'Green'})
  if($Mutation){
    $mms=LiveMs $Mutation $Now
    $m1="$(Badge ([string]$Mutation.State) $mms)  $($Mutation.Operation)  $mms ms"
    $m2='WHAT: '+(Clip ([string]$Mutation.Detail) ($lw-10))
    $mc=StateColor ([string]$Mutation.State) $mms
  }else{$m1='none';$m2='No mutation in current activity window.';$mc='DarkGray'}
  if($Failure){
    $fms=LiveMs $Failure $Now
    $f1="$(Badge ([string]$Failure.State) $fms)  $($Failure.Operation)  $fms ms"
    $f2='WHY: '+(Clip ([string]$Failure.Detail) ($rw-10))
    $fc='Red'
  }else{$f1='none';$f2='No recent failed, blocked, or timed-out operations.';$fc='Green'}
  foreach($pair in @(@($m1,$f1),@($m2,$f2))){
    C ('|'+(Pad (' '+$pair[0]) ($lw-2))+'|') $mc -N
    C (' '*$gap) DarkGray -N
    C ('|'+(Pad (' '+$pair[1]) ($rw-2))+'|') $fc
  }
  C ('+'+(Line ($lw-2) '-')+'+') Magenta -N
  C (' '*$gap) DarkGray -N
  C ('+'+(Line ($rw-2) '-')+'+') $(if($Failure){'Red'}else{'Green'})
}
function Footer([int]$Width,$Active,$Queued,$Failed,$P95){
  $status=if($Failed){'ATTENTION'}elseif($P95 -ge $StallMs){'STALL RISK'}elseif($Active){'WORKING'}else{'READY'}
  $sc=if($status -eq 'READY'){'Green'}elseif($status -eq 'WORKING'){'Cyan'}else{'Yellow'}
  Border $Width DarkCyan
  C '| STATUS ' DarkGray -N; C $status $sc -N; C ' | ' DarkGray -N
  $summary="active=$Active queued=$Queued failed=$Failed p95=$P95 ms"
  C (Pad $summary ([Math]::Max(20,$Width-30-$status.Length))) Gray -N
  C (' refresh='+$RefreshMs+'ms |') DarkGray
  C ('| '+(Pad ('log: '+$EventLogPath) ($Width-4))+' |') DarkGray
  Border $Width DarkCyan
}

$first=$true
while($true){
  $now=[DateTimeOffset]::Now
  $events=ReadEvents
  $ops=LatestOps $events
  $last=if($events.Count){$events[-1]}else{$null}
  $active=if($last -and $null -ne $last.Active){[int]$last.Active}else{@($ops|Where-Object State -eq 'RUNNING').Count}
  $queued=if($last -and $null -ne $last.Queued){[int]$last.Queued}else{@($ops|Where-Object State -eq 'QUEUED').Count}
  $success=@($ops|Where-Object State -eq 'SUCCESS').Count
  $failed=@($ops|Where-Object {$_.State -in @('FAILED','TIMEOUT','BLOCKED')}).Count
  $samples=@($events|Where-Object State -eq 'SUCCESS'|Select-Object -Last 100|ForEach-Object{[int64]$_.ElapsedMs})
  $median=Pct $samples 50
  $p95=Pct $samples 95
  $mut=@($ops|Where-Object Lane -eq 'MUTATE');$mutation=if($mut.Count){$mut[-1]}else{$null}
  $fail=@($ops|Where-Object {$_.State -in @('FAILED','TIMEOUT','BLOCKED')});$failure=if($fail.Count){$fail[-1]}else{$null}
  $width=148
  try{$width=[Math]::Max(112,[Math]::Min(180,$Host.UI.RawUI.WindowSize.Width))}catch{}
  if($first){Clear-Host;$first=$false}else{try{[Console]::SetCursorPosition(0,0)}catch{Clear-Host}}
  Header $width $now
  Cards $width $active $queued $success $failed $median $p95
  Activity $ops $width $now
  Dual $mutation $failure $width $now
  Footer $width $active $queued $failed $p95
  if($Once){break}
  Start-Sleep -Milliseconds $RefreshMs
}
