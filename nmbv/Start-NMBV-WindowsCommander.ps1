[CmdletBinding()]
param(
  [string]$RuntimeConfigPath = (Join-Path $PSScriptRoot 'runtime.json')
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Get-ObjectProperty($Object,[string]$Name,$Default=$null) {
  if($null -eq $Object){return $Default}
  $prop=$Object.PSObject.Properties[$Name]
  if($null -eq $prop){return $Default}
  return $prop.Value
}

function Clamp-Int([int]$Value,[int]$Minimum,[int]$Maximum) {
  if($Value -lt $Minimum){return $Minimum}
  if($Value -gt $Maximum){return $Maximum}
  return $Value
}

if(-not (Test-Path -LiteralPath $RuntimeConfigPath)){
  throw "Windows Commander runtime config was not found: $RuntimeConfigPath"
}

$cfg=Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
$runtimeApiKeyRef=[string](Get-ObjectProperty $cfg 'runtimeApiKeyRef' '')
if($runtimeApiKeyRef -match '^env:(.+)$'){
  $runtimeKeyName=$Matches[1]
  $runtimeKeyValue=[Environment]::GetEnvironmentVariable($runtimeKeyName,'Process')
  if([string]::IsNullOrWhiteSpace($runtimeKeyValue)){
    $runtimeKeyValue=[Environment]::GetEnvironmentVariable($runtimeKeyName,'User')
    if([string]::IsNullOrWhiteSpace($runtimeKeyValue)){
      $runtimeKeyValue=[Environment]::GetEnvironmentVariable($runtimeKeyName,'Machine')
    }
    if(-not [string]::IsNullOrWhiteSpace($runtimeKeyValue)){
      [Environment]::SetEnvironmentVariable($runtimeKeyName,$runtimeKeyValue,'Process')
    }
  }
}
foreach($required in @('alias','tunnelId','runtimeApiKeyRef','tunnelClientPath','executable')){
  $value=[string](Get-ObjectProperty $cfg $required '')
  if([string]::IsNullOrWhiteSpace($value)){
    throw "Windows Commander runtime config is missing '$required'."
  }
}

if(-not (Test-Path -LiteralPath ([string]$cfg.tunnelClientPath))){
  throw "tunnel-client was not found: $($cfg.tunnelClientPath)"
}
if(-not (Test-Path -LiteralPath ([string]$cfg.executable))){
  throw "Windows Commander executable was not found: $($cfg.executable)"
}

$mcpCommand=([string]$cfg.executable) -replace '\\','/'
$instanceKey=[string](Get-ObjectProperty $cfg 'instanceKey' $cfg.alias)
$unattended=[bool](Get-ObjectProperty $cfg 'unattended' $true)
$maxConcurrency=Clamp-Int ([int](Get-ObjectProperty $cfg 'maxConcurrency' 6)) 1 10
$requestTimeoutMs=Clamp-Int ([int](Get-ObjectProperty $cfg 'requestTimeoutMs' 90000)) 5000 110000
$activityLogPath=[string](Get-ObjectProperty $cfg 'activityLogPath' '')
$serenaRoot=[string](Get-ObjectProperty $cfg 'serenaRoot' '')
$serenaStartScript=[string](Get-ObjectProperty $cfg 'serenaStartScript' '')

$env:WINDOWS_COMMANDER_INSTANCE_KEY=$instanceKey
$env:WINDOWS_COMMANDER_UNATTENDED=if($unattended){'1'}else{'0'}
$env:WINDOWS_COMMANDER_MAX_CONCURRENCY=[string]$maxConcurrency
$env:WINDOWS_COMMANDER_REQUEST_TIMEOUT_MS=[string]$requestTimeoutMs
if(-not [string]::IsNullOrWhiteSpace($activityLogPath)){
  $env:WINDOWS_COMMANDER_ACTIVITY_LOG=[Environment]::ExpandEnvironmentVariables($activityLogPath)
}
if(-not [string]::IsNullOrWhiteSpace($serenaRoot)){
  $env:WINDOWS_COMMANDER_SERENA_ROOT=[Environment]::ExpandEnvironmentVariables($serenaRoot)
}
if(-not [string]::IsNullOrWhiteSpace($serenaStartScript)){
  $env:WINDOWS_COMMANDER_SERENA_START_SCRIPT=[Environment]::ExpandEnvironmentVariables($serenaStartScript)
}

function Invoke-TunnelClient([string[]]$Arguments){
  $saved=$ErrorActionPreference
  try{
    $ErrorActionPreference='Continue'
    $output=& $cfg.tunnelClientPath @Arguments 2>&1
    $code=$LASTEXITCODE
  }finally{$ErrorActionPreference=$saved}
  [pscustomobject]@{ExitCode=$code;Output=($output|Out-String)}
}

function Status {
  $r=Invoke-TunnelClient @('runtimes','status',$cfg.alias,'--json')
  if($r.ExitCode -ne 0 -or -not $r.Output.Trim()){return $null}
  try{$r.Output|ConvertFrom-Json}catch{return $null}
}

function Test-ManagedReady($Status) {
  if($null -eq $Status){return $false}
  if((Get-ObjectProperty $Status 'process_running' $false) -ne $true){return $false}
  if((Get-ObjectProperty $Status 'healthy' $false) -ne $true){return $false}
  if((Get-ObjectProperty $Status 'ready' $false) -ne $true){return $false}
  if((Get-ObjectProperty $Status 'tunnel_id' '') -ne $cfg.tunnelId){return $false}

  $process=Get-ObjectProperty $Status 'process' $null
  $target=[string](Get-ObjectProperty $process 'target_value' '')
  if([string]::IsNullOrWhiteSpace($target)){return $false}

  $actual=($target -replace '\\','/').Trim('"',"'")
  return $actual.Equals($mcpCommand,[StringComparison]::OrdinalIgnoreCase)
}

function Stop-UnmanagedRawProfileOwners {
  $alias=[regex]::Escape([string]$cfg.alias)
  $configuredExe=[IO.Path]::GetFullPath([string]$cfg.tunnelClientPath).Replace('/','\')

  function Get-RawOwners {
    @(Get-CimInstance Win32_Process -Filter "Name='tunnel-client.exe'" -ErrorAction SilentlyContinue |
      Where-Object {
        $commandLine=[string]$_.CommandLine
        $executablePath=[string]$_.ExecutablePath
        if([string]::IsNullOrWhiteSpace($commandLine) -or [string]::IsNullOrWhiteSpace($executablePath)){return $false}

        $sameExecutable=[IO.Path]::GetFullPath($executablePath).Replace('/','\').Equals(
          $configuredExe,
          [StringComparison]::OrdinalIgnoreCase)
        $isRawProfile=$commandLine -match "(?i)\brun\s+--profile\s+[`"']?$alias[`"']?(?:\s|$)"
        $isManaged=$commandLine -match '(?i)\brun\s+--profile-dir\b'
        return $sameExecutable -and $isRawProfile -and -not $isManaged
      })
  }

  function Stop-RawOwnerAndWatchdog($Process) {
    $parentId=[int]$Process.ParentProcessId
    if($parentId -gt 0){
      $parent=Get-CimInstance Win32_Process -Filter "ProcessId=$parentId" -ErrorAction SilentlyContinue
      $parentName=[string]$parent.Name
      if($parent -and $parentName -in @('cmd.exe','powershell.exe','pwsh.exe')){
        # A raw Windows Commander profile launched from one of these shells can
        # be a restart loop. Kill the dedicated parent immediately instead of
        # waiting for the child to respawn and retake tunnel ownership.
        Stop-Process -Id $parentId -Force -ErrorAction SilentlyContinue
      }
    }

    Stop-Process -Id $Process.ProcessId -Force -ErrorAction SilentlyContinue
  }

  [array]$owners=Get-RawOwners
  if(@($owners).Count -eq 0){return}

  foreach($process in $owners){
    Stop-RawOwnerAndWatchdog $process
  }

  # Wait beyond the historical 2-second raw .cmd restart cadence.
  Start-Sleep -Milliseconds 2500

  [array]$remaining=Get-RawOwners
  if(@($remaining).Count -gt 0){
    foreach($process in $remaining){
      Stop-RawOwnerAndWatchdog $process
    }
    Start-Sleep -Milliseconds 1000
    [array]$remaining=Get-RawOwners
  }

  if(@($remaining).Count -gt 0){
    $pids=($remaining | ForEach-Object { $_.ProcessId }) -join ','
    throw "Unable to retire raw Windows Commander profile owner(s): $pids"
  }
}

if($TakeManagedOwnership){
  Stop-UnmanagedRawProfileOwners
}else{
  $rawAlias=[regex]::Escape([string]$cfg.alias)
  $rawTunnelExe=[IO.Path]::GetFullPath([string]$cfg.tunnelClientPath).Replace('/','\')
  [array]$rawOwners=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
      $commandLine=[string]$_.CommandLine
      $executablePath=[string]$_.ExecutablePath
      if([string]::IsNullOrWhiteSpace($commandLine) -or [string]::IsNullOrWhiteSpace($executablePath)){return $false}

      $sameExecutable=[IO.Path]::GetFullPath($executablePath).Replace('/','\').Equals(
        $rawTunnelExe,
        [StringComparison]::OrdinalIgnoreCase)
      $isRawProfile=$commandLine -match "(?i)\brun\s+--profile\s+[`"']?$rawAlias[`"']?(?:\s|$)"
      $isManaged=$commandLine -match '(?i)\brun\s+--profile-dir\b'
      return $sameExecutable -and $isRawProfile -and -not $isManaged
    })

  if(@($rawOwners).Count -gt 0){
    Write-Host "Windows Commander raw compatibility runtime is active; leaving it running. Use -TakeManagedOwnership only for an explicit cutover."
    return
  }
}

$s=Status
if(Test-ManagedReady $s){
  return
}

if($s){
  [void](Invoke-TunnelClient @('runtimes','stop',$cfg.alias,'--json'))
  for($i=0;$i -lt 20;$i++){
    Start-Sleep -Milliseconds 500
    $s=Status
    if(-not $s -or (Get-ObjectProperty $s 'process_running' $false) -ne $true){break}
  }

  $rm=Invoke-TunnelClient @('runtimes','rm',$cfg.alias,'--json')
  if($rm.ExitCode -ne 0 -and $rm.Output -notmatch '(?i)(not known|does not exist|not found)'){
    # runtimes rm can remove the managed alias/process but still report a
    # cleanup error when an old log/health file is momentarily locked. Re-read
    # authoritative runtime state before treating the command result as fatal.
    $postRemove=Status
    if($postRemove -and (Get-ObjectProperty $postRemove 'process_running' $false) -eq $true){
      throw "Unable to remove unhealthy Windows Commander runtime: $($rm.Output.Trim())"
    }
  }
}

$lastError=''
for($attempt=1;$attempt -le 3;$attempt++){
  $connect=Invoke-TunnelClient @(
    'runtimes','connect',
    '--alias',$cfg.alias,
    '--tunnel-id',$cfg.tunnelId,
    '--runtime-api-key',$cfg.runtimeApiKeyRef,
    '--mcp-command',$mcpCommand,
    '--json'
  )

  if($connect.ExitCode -eq 0){
    for($i=0;$i -lt 45;$i++){
      Start-Sleep -Seconds 1
      $s=Status
      if(Test-ManagedReady $s){
        return
      }
    }
    $lastError='managed runtime did not become ready with the configured executable'
  }else{
    $lastError=$connect.Output.Trim()
  }

  if($attempt -lt 3){
    Start-Sleep -Seconds ([int][Math]::Pow(2,$attempt-1))
  }
}

throw "Windows Commander managed runtime failed startup: $lastError"
