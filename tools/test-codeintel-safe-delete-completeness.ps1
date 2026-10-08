[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ServerExe,
  [Parameter(Mandatory=$true)][string]$LanguageServerCli,
  [Parameter(Mandatory=$true)][string]$NodeExe
)
$ErrorActionPreference='Stop'
if(!(Test-Path $ServerExe) -or !(Test-Path $LanguageServerCli) -or !(Test-Path $NodeExe)){throw 'Missing supplied executable'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('wcm-delete-guard-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$domain=Join-Path $root 'domain.mjs'
$consumer=Join-Path $root 'consumer.mjs'
[IO.File]::WriteAllText($domain,"export function used(){return 'ok';}" + [Environment]::NewLine)
[IO.File]::WriteAllText($consumer,"import {used} from './domain.mjs';" + [Environment]::NewLine + 'console.log(used());' + [Environment]::NewLine)
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName=$ServerExe
$psi.UseShellExecute=$false
$psi.RedirectStandardInput=$true
$psi.RedirectStandardOutput=$true
$psi.RedirectStandardError=$false
$psi.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
$psi.Environment['WINDOWS_COMMANDER_UNATTENDED']='1'
$psi.Environment['WINDOWS_COMMANDER_INSTANCE_KEY']='isolated-delete-safety-test'
$proc=[Diagnostics.Process]::Start($psi)
if(!$proc){throw 'Unable to start isolated MCP'}
$sequence=0
function Invoke-TestMcp([string]$tool,[hashtable]$toolArgs){
  $script:sequence++
  $request=@{jsonrpc='2.0';id=$script:sequence;method='tools/call';params=@{name=$tool;arguments=$toolArgs}}|ConvertTo-Json -Depth 12 -Compress
  $proc.StandardInput.WriteLine($request)
  $proc.StandardInput.Flush()
  $line=$proc.StandardOutput.ReadLine()
  if(!$line){throw ('Missing MCP response for '+$tool)}
  $reply=$line|ConvertFrom-Json
  if($reply.error){throw ($tool+': '+($reply.error|ConvertTo-Json -Compress))}
  if(!$reply.result.content -or $reply.result.content[0].type -ne 'text'){throw ('MCP content invalid for '+$tool)}
  return $reply.result.content[0].text|ConvertFrom-Json
}
try {
  $started=Invoke-TestMcp 'codeintel_start' @{executable_path=$NodeExe;arguments=@($LanguageServerCli,'--stdio');workspace_root=$root;language_id='javascript'}
  $sid=[string]$started.session_id
  if(!$sid){throw 'No LSP session'}
  $null=Invoke-TestMcp 'codeintel_symbols' @{session_id=$sid;path=$domain}
  $early=Invoke-TestMcp 'codeintel_safe_delete_preflight' @{session_id=$sid;path=$domain;line=0;character=17}
  if($early.reference_count -ne 0){throw 'Expected no references in not-yet-loaded consumer'}
  if($early.safe_to_delete -ne $false -or $early.reference_index_complete -ne $false){throw 'UNSAFE: zero references treated as safe deletion'}
  $null=Invoke-TestMcp 'codeintel_symbols' @{session_id=$sid;path=$consumer}
  $later=Invoke-TestMcp 'codeintel_safe_delete_preflight' @{session_id=$sid;path=$domain;line=0;character=17}
  if($later.reference_count -lt 2 -or $later.safe_to_delete -ne $false){throw 'UNSAFE: consumer reference detection not honored'}
  $null=Invoke-TestMcp 'codeintel_stop' @{session_id=$sid}
  Write-Output ('PASS codeintel_safe_delete: incomplete_graph=false_allowed, references_after_load='+$later.reference_count)
}finally{
  $proc.StandardInput.Close()
  if(!$proc.WaitForExit(3500)){$proc.Kill();$proc.WaitForExit()}
  $proc.Dispose()
  [IO.Directory]::Delete($root,$true)
}
