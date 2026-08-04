$ErrorActionPreference='Stop'; $failures=0
function Assert($condition,[string]$message){if(!$condition){$script:failures++;Write-Error "FAIL $message" -ErrorAction Continue}else{Write-Host "PASS $message"}}
$base=Join-Path $env:TEMP ("FindFast installer tests $([guid]::NewGuid().ToString('N'))");$payload=Join-Path $base 'payload with spaces';$install=Join-Path $base 'installed app';$data=Join-Path $base 'data dir';$root=Join-Path $base 'source root';$fake=Join-Path $base 'fake cli';New-Item -ItemType Directory -Path $payload,$root,$fake -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $payload 'FindFast.Server.exe'),'fake')
$config=Join-Path $base 'config.json';@{roots=@(@{path=$root;name='Root With Spaces';extensions=@('CS','.json');include=@('src/**');exclude=@('src/bin/**');respect_gitignore=$true})}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $config -Encoding UTF8
$installer=Join-Path $PSScriptRoot '..\Install-FindFast.ps1';$uninstaller=Join-Path $PSScriptRoot '..\Uninstall-FindFast.ps1'
$emptyInstall=Join-Path $base 'empty install';$emptyData=Join-Path $base 'empty data';& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $emptyInstall -DataDirectory $emptyData -SkipIndex -SkipClientRegistration
Assert ($LASTEXITCODE -eq 0) 'headless install without roots succeeds';$emptyCatalogRaw=Get-Content -Raw (Join-Path $emptyData 'roots.json');$null=$emptyCatalogRaw|ConvertFrom-Json;Assert (($emptyCatalogRaw -replace '\s','') -eq '[]') 'empty catalog is valid JSON array'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -SkipClientRegistration
Assert ($LASTEXITCODE -eq 0) 'headless install succeeds';$catalog=@(Get-Content -Raw (Join-Path $data 'roots.json')|ConvertFrom-Json);Assert ($catalog.Count-eq 1) 'catalog root merged';Assert (($catalog[0].extensions -join ',')-eq'.cs,.json') 'extensions normalized';Assert (Test-Path (Join-Path $install 'FindFast.Server.exe')) 'payload installed with spaced paths'
# Fake client CLIs prove safe argument construction and idempotence without touching real clients.
$fakeTemplate=@'
@echo off
echo %*>>"%FAKE_MCP_LOG%"
if "%2"=="get" (
 if exist "%FAKE_MCP_STATE%.%~n0" (type "%FAKE_MCP_STATE%.%~n0" & exit /b 0) else exit /b 1
)
if "%FAKE_MCP_FAIL%"=="1" if "%2"=="add" exit /b 5
if "%2"=="add" (echo %* >"%FAKE_MCP_STATE%.%~n0" & exit /b 0)
if "%2"=="remove" (del /q "%FAKE_MCP_STATE%.%~n0" 2>nul & exit /b 0)
exit /b 0
'@
[IO.File]::WriteAllText((Join-Path $fake 'codex.cmd'),$fakeTemplate);[IO.File]::WriteAllText((Join-Path $fake 'claude.cmd'),$fakeTemplate)
$oldPath=$env:PATH;$env:PATH="$fake;$oldPath";$env:FAKE_MCP_LOG=Join-Path $base 'mcp.log';$env:FAKE_MCP_STATE=Join-Path $base 'mcp.state'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex
$clientLog=Get-Content -Raw $env:FAKE_MCP_LOG;Assert ($clientLog -like '*--env*FINDFAST_DATA_DIR=*') 'fake CLI receives environment argument';Assert ($clientLog -like '*--transport stdio*--scope user*') 'Claude command uses official transport/scope flags';Assert ($clientLog -like "*$install*") 'installed executable path passed as one quoted argument'
$installedExe=Join-Path $install 'FindFast.Server.exe';@{transport=@{command=$installedExe;args=@()}}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath "$env:FAKE_MCP_STATE.codex";[IO.File]::WriteAllText("$env:FAKE_MCP_STATE.claude",("Command: `"$installedExe`""))
$addsBefore=([regex]::Matches($clientLog,'mcp add')).Count
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex
$addsAfter=([regex]::Matches((Get-Content -Raw $env:FAKE_MCP_LOG),'mcp add')).Count;Assert ($addsAfter -eq $addsBefore) 'client registration parses escaped Codex JSON and Claude output'
$divergent=Join-Path $base 'other app\FindFast.Server.exe';@{command=$divergent}|ConvertTo-Json|Set-Content "$env:FAKE_MCP_STATE.codex";$beforeConflict=Get-Content -Raw $env:FAKE_MCP_LOG
$conflictOut=& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex 2>&1|Out-String
Assert ($conflictOut -like '*CONFLITO*codex*preservado*') 'divergent registration is preserved and summarized';Assert ((Get-Content -Raw $env:FAKE_MCP_LOG) -notlike "$beforeConflict*mcp remove findfast*") 'conflict does not remove without consent'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -UpdateClientConflicts
$afterOptIn=Get-Content -Raw $env:FAKE_MCP_LOG;Assert ($afterOptIn -like '*mcp remove findfast*') 'opt-in conflict update removes old registration';Assert (([regex]::Matches($afterOptIn,'mcp add')).Count -gt $addsAfter) 'opt-in conflict update adds replacement'
Remove-Item "$env:FAKE_MCP_STATE.codex","$env:FAKE_MCP_STATE.claude" -Force -ErrorAction SilentlyContinue;$env:FAKE_MCP_FAIL='1';$policyOut=& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex 2>&1|Out-String;Assert ($LASTEXITCODE -eq 0) 'client policy failure does not abort install';Assert ($policyOut -like '*Falha registrando codex*' -and $policyOut -like '*Falha registrando claude*') 'client policy failures are explicit in summary';Remove-Item Env:FAKE_MCP_FAIL
$env:PATH=$oldPath
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -SkipClientRegistration
$catalog=@(Get-Content -Raw (Join-Path $data 'roots.json')|ConvertFrom-Json);Assert ($catalog.Count-eq 1) 'upgrade/idempotence avoids duplicate roots'
& powershell -NoProfile -ExecutionPolicy Bypass -File $uninstaller -InstallDirectory $install -DataDirectory $data -FilesManagedByInstaller
Assert (Test-Path $install) 'Inno-managed uninstall leaves app deletion to Inno';Assert (Test-Path (Join-Path $data 'roots.json')) 'Inno-managed uninstall preserves data'
& powershell -NoProfile -ExecutionPolicy Bypass -File $uninstaller -InstallDirectory $install -DataDirectory $data
Assert (!(Test-Path $install)) 'uninstall removes binaries';Assert (Test-Path (Join-Path $data 'roots.json')) 'uninstall preserves data by default';Assert (Test-Path $root) 'uninstall preserves source root'
# Invalid roots fail before replacing an existing installation.
New-Item -ItemType Directory -Path $install -Force|Out-Null;[IO.File]::WriteAllText((Join-Path $install 'marker'),'old');$bad=Join-Path $base 'bad.json';@{roots=@(@{path=(Join-Path $base 'missing')})}|ConvertTo-Json -Depth 4|Set-Content $bad
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $bad -SkipIndex -SkipClientRegistration
Assert ($LASTEXITCODE -eq 1) 'invalid root returns failure';Assert (Test-Path (Join-Path $install 'marker')) 'validation failure preserves previous install'
$catalogBefore=Get-Content -Raw (Join-Path $data 'roots.json');& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -SkipClientRegistration -TestFailBeforeCatalogPublish
Assert ($LASTEXITCODE -eq 1) 'catalog publication fault returns failure';Assert (Test-Path (Join-Path $install 'marker')) 'catalog fault rolls binaries back';Assert ((Get-Content -Raw (Join-Path $data 'roots.json')) -eq $catalogBefore) 'catalog temporary failure preserves previous JSON'
# An executable that cannot speak MCP yields partial-success code 2, without rollback of installed binaries/catalog.
$partialPayload=Join-Path $base 'partial payload';New-Item -ItemType Directory $partialPayload|Out-Null;Copy-Item "$env:SystemRoot\System32\where.exe" (Join-Path $partialPayload 'FindFast.Server.exe')
$partialConfig=Join-Path $base 'partial.json';@{roots=@(@{path=$root;name='Root With Spaces';extensions=@('txt');include=@();exclude=@();respect_gitignore=$true})}|ConvertTo-Json -Depth 8|Set-Content $partialConfig
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $partialPayload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $partialConfig -SkipClientRegistration -IndexTimeoutSeconds 5
Assert ($LASTEXITCODE -eq 2) 'indexing failure returns partial-success code';Assert (Test-Path (Join-Path $install 'FindFast.Server.exe')) 'partial indexing keeps completed installation'
Remove-Item -LiteralPath $base -Recurse -Force
if($failures){throw "$failures installer tests failed"};Write-Host 'ALL INSTALLER TESTS PASSED'
