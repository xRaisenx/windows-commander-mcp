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

$cfg=Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
$mcpCommand=([string]$cfg.executable) -replace '\\','/'

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
  return (Get-ObjectProperty $Status 'process_running' $false) -eq $true -and
    (Get-ObjectProperty $Status 'healthy' $false) -eq $true -and
    (Get-ObjectProperty $Status 'ready' $false) -eq $true -and
    (Get-ObjectProperty $Status 'tunnel_id' '') -eq $cfg.tunnelId
}

function Stop-UnmanagedRawProfileOwners {
  $alias=[regex]::Escape([string]$cfg.alias)
  $configuredExe=[IO.Path]::GetFullPath([string]$cfg.tunnelClientPath).Replace('/','\')

  $matches=Get-CimInstance Win32_Process -Filter "Name='tunnel-client.exe'" -ErrorAction SilentlyContinue |
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
    }

  foreach($process in $matches) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
  }
}

$s=Status
if(Test-ManagedReady $s){
  return
}

# Enforce one ownership model per alias. A manually launched
# "tunnel-client run --profile <alias>" process can keep the health URL live
# while the managed runtime record is stopped, creating split-brain readiness.
Stop-UnmanagedRawProfileOwners

if($s){
  $stop=Invoke-TunnelClient @('runtimes','stop',$cfg.alias,'--json')
  for($i=0;$i -lt 20;$i++){
    Start-Sleep -Milliseconds 500
    $s=Status
    if(-not $s -or (Get-ObjectProperty $s 'process_running' $false) -ne $true){break}
  }
  $rm=Invoke-TunnelClient @('runtimes','rm',$cfg.alias,'--json')
  if($rm.ExitCode -ne 0 -and $rm.Output -notmatch '(?i)(not known|does not exist|not found)'){
    throw "Unable to remove unhealthy Windows Commander runtime: $($rm.Output.Trim())"
  }
}

$connect=Invoke-TunnelClient @(
  'runtimes','connect',
  '--alias',$cfg.alias,
  '--tunnel-id',$cfg.tunnelId,
  '--runtime-api-key',$cfg.runtimeApiKeyRef,
  '--mcp-command',$mcpCommand,
  '--json'
)
if($connect.ExitCode -ne 0){throw "Unable to start Windows Commander runtime: $($connect.Output.Trim())"}

for($i=0;$i -lt 45;$i++){
  Start-Sleep -Seconds 1
  $s=Status
  if(Test-ManagedReady $s){
    return
  }
}
throw 'Windows Commander managed runtime failed startup readiness.'
