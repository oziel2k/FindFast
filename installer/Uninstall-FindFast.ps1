[CmdletBinding()] param([string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs\FindFast'),[string]$DataDirectory=(Join-Path $env:LOCALAPPDATA 'FindFast'),[switch]$RemoveData,[switch]$RemoveClientRegistrations,[switch]$FilesManagedByInstaller)
$exe=Join-Path $InstallDirectory 'FindFast.Server.exe'
if($RemoveClientRegistrations){foreach($client in 'codex','claude'){$cmd=Get-Command $client -ErrorAction SilentlyContinue;if($cmd){$get=& $cmd.Source mcp get findfast --json 2>&1|Out-String;if($LASTEXITCODE -ne 0 -and $client -eq 'claude'){$get=& $cmd.Source mcp get findfast 2>&1|Out-String};if($get -like "*$exe*"){& $cmd.Source mcp remove findfast}}}}
if(!$FilesManagedByInstaller -and (Test-Path -LiteralPath $InstallDirectory)){Remove-Item -LiteralPath $InstallDirectory -Recurse -Force}
if($RemoveData -and (Test-Path -LiteralPath $DataDirectory)){Remove-Item -LiteralPath $DataDirectory -Recurse -Force}
Write-Host "FindFast removido. Dados preservados=$(!$RemoveData). Raízes fonte nunca são apagadas."
