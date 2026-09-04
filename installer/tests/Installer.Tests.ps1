$ErrorActionPreference='Stop'; $failures=0
function Assert($condition,[string]$message){if(!$condition){$script:failures++;Write-Error "FAIL $message" -ErrorAction Continue}else{Write-Host "PASS $message"}}
$base=Join-Path $env:TEMP ("FindFast installer tests $([guid]::NewGuid().ToString('N'))");$payload=Join-Path $base 'payload with spaces';$install=Join-Path $base 'installed app';$data=Join-Path $base 'data dir';$root=Join-Path $base 'source root';$fake=Join-Path $base 'fake cli';New-Item -ItemType Directory -Path $payload,$root,$fake -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $payload 'FindFast.Server.exe'),'fake')
$config=Join-Path $base 'config.json';@{roots=@(@{path=$root;name='Root With Spaces';extensions=@('CS','.json');include=@('src/**');exclude=@('src/bin/**');respect_gitignore=$true})}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $config -Encoding UTF8
$installer=Join-Path $PSScriptRoot '..\Install-FindFast.ps1';$uninstaller=Join-Path $PSScriptRoot '..\Uninstall-FindFast.ps1'
$inno=Get-Content -Raw (Join-Path $PSScriptRoot '..\FindFast.iss')
# Setup e binário precisam anunciar a mesma versão, ou "qual build está instalado?" fica sem resposta.
$issVersion=[regex]::Match($inno,'#define\s+AppVersion\s+"([^"]+)"').Groups[1].Value
$propsVersion=[regex]::Match((Get-Content -Raw (Join-Path $PSScriptRoot '..\..\Directory.Build.props')),'<Version>([^<]+)</Version>').Groups[1].Value
Assert ($issVersion -match '^\d+\.\d+\.\d+$') "AppVersion is a three-part version ($issVersion)"
Assert ($issVersion -eq $propsVersion) "installer and assembly versions match ($issVersion vs $propsVersion)"
Assert ($inno -match 'RootFieldCount\s*=\s*(\d+)' -and [int]$Matches[1] -ge 5) 'Inno wizard offers at least five folder fields'
$rootFieldCount=[int]([regex]::Match($inno,'RootFieldCount\s*=\s*(\d+)').Groups[1].Value)
Assert ($inno -match "array\[0\.\.$($rootFieldCount-1)\] of TNewEdit" -and $inno -match "array\[0\.\.$($rootFieldCount-1)\] of TNewButton") 'folder field arrays match RootFieldCount'
Assert (([regex]::Matches($inno,'for I := 0 to RootFieldCount - 1 do')).Count -ge 4) 'every folder loop is bounded by RootFieldCount'
Assert ($inno -match 'BrowseForFolder' -and $inno -match 'RootBrowseButtons\[I\]\.OnClick := @RootBrowseClick') 'each folder field has a working browse button'
Assert ($inno -match 'CreateWizardRootConfig' -and $inno -match 'SaveStringToFile') 'Inno wizard serializes roots configuration'
Assert ($inno -match 'A pasta foi informada mais de uma vez') 'Inno wizard rejects the same folder entered twice'
Assert ($inno -match 'ExtensionsMemo\s*:\s*TNewMemo' -and $inno -match 'ExtensionsMemo\.Text := DefaultExtensionsText' -and $inno -match 'ExtensionsJson') 'Inno wizard prefills an editable extension list'
Assert ($inno -match "Caption := 'Restaurar padr" -and $inno -match "Caption := 'Todas \(\*\)'" -and $inno -match "Caption := 'Limpar'") 'extension page exposes restore/all/clear shortcuts'
# The suggested list is a copy of the server's set; drift would silently narrow or widen new roots.
$issDefaults=@([regex]::Matches([regex]::Match($inno,'(?s)function DefaultExtensionsText: String;\s*begin(.*?)end;').Groups[1].Value,'\.[A-Za-z0-9_-]+')|ForEach-Object{$_.Value})
$coreSource=Get-Content -Raw (Join-Path $PSScriptRoot '..\..\src\FindFast.Core\FindFastService.cs')
$coreDefaults=@([regex]::Matches([regex]::Match($coreSource,'(?s)DefaultExtensions\s*=\s*\[(.*?)\];').Groups[1].Value,'"(\.[A-Za-z0-9_-]+)"')|ForEach-Object{$_.Groups[1].Value})
Assert ($coreDefaults.Count -gt 80) 'server default extension set was located'
Assert (($issDefaults -join ',') -eq ($coreDefaults -join ',')) 'wizard suggests exactly the server default extension set'
# An untouched suggestion must serialize as [] so the root keeps following future server defaults.
Assert ($inno -match 'if Body = DefaultBody then Result := '''' else Result := Body') 'unedited suggestion collapses to the server default set'
Assert ($inno -match 'if HasStar then' -and $inno -match "Result := '\""\*\""'") 'star token is emitted alone, matching server precedence'
Assert ($inno -match 'FilesAlreadyInstalled -Headless' -and $inno -match 'Flags: runhidden waituntilterminated') 'Inno bootstrap runs hidden without console prompts'
$emptyInstall=Join-Path $base 'empty install';$emptyData=Join-Path $base 'empty data';& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $emptyInstall -DataDirectory $emptyData -SkipIndex -SkipClientRegistration
Assert ($LASTEXITCODE -eq 0) 'headless install without roots succeeds';$emptyCatalogRaw=Get-Content -Raw (Join-Path $emptyData 'roots.json');$null=$emptyCatalogRaw|ConvertFrom-Json;Assert (($emptyCatalogRaw -replace '\s','') -eq '[]') 'empty catalog is valid JSON array'
$legacyInstall=Join-Path $base 'legacy install';$legacyData=Join-Path $base 'legacy data';New-Item -ItemType Directory -Path $legacyData -Force|Out-Null
$legacyRoot=[pscustomobject]@{root_id='legacy-object';name='legacy-object';path=$root;type='directory';include=[pscustomobject]@{};exclude=$null;extensions=[pscustomobject]@{};respect_gitignore=$true;state='stale';version=0;file_count=0};[IO.File]::WriteAllText((Join-Path $legacyData 'roots.json'),(ConvertTo-Json -InputObject @($legacyRoot) -Depth 5))
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $legacyInstall -DataDirectory $legacyData -SkipIndex -SkipClientRegistration
$legacyCatalogRaw=Get-Content -Raw (Join-Path $legacyData 'roots.json');Assert ($LASTEXITCODE -eq 0) 'legacy malformed catalog is migrated';Assert ($legacyCatalogRaw -notmatch '"(include|exclude|extensions)"\s*:\s*(null|\{)') 'catalog collection fields are normalized to arrays'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -SkipClientRegistration
Assert ($LASTEXITCODE -eq 0) 'headless install succeeds';$catalog=@(Get-Content -Raw (Join-Path $data 'roots.json')|ConvertFrom-Json);Assert ($catalog.Count-eq 1) 'catalog root merged';Assert (($catalog[0].extensions -join ',')-eq'.cs,.json') 'extensions normalized';$starConfig=Join-Path $base 'star.json';@{roots=@(@{path=$root;extensions=@('*');include=@();exclude=@();respect_gitignore=$true})}|ConvertTo-Json -Depth 8|Set-Content $starConfig;& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $starConfig -SkipIndex -SkipClientRegistration;$starCatalog=@(Get-Content -Raw (Join-Path $data 'roots.json')|ConvertFrom-Json);Assert ((@($starCatalog[0].extensions) -join ',') -eq '*') 'star token survives extension normalization';Assert (Test-Path (Join-Path $install 'FindFast.Server.exe')) 'payload installed with spaced paths'
# Fake client CLIs prove safe argument construction and idempotence without touching real clients.
$fakeTemplate=@'
@echo off
setlocal EnableDelayedExpansion
echo %*>>"%FAKE_MCP_LOG%"
if "%2"=="get" (
 if exist "%FAKE_MCP_STATE%.%~n0" (
  set /p target=<"%FAKE_MCP_STATE%.%~n0"
  if "%~n0"=="codex" (echo {"command":"!target:\=\\!"}) else echo Command: "!target!"
  exit /b 0
 ) else (
  echo No MCP server named findfast found. 1>&2
  exit /b 1
 )
)
if "%FAKE_MCP_FAIL%"=="1" if "%2"=="add" exit /b 5
if "%2"=="add" (
 set "target="
 for %%A in (%*) do set "target=%%~A"
 echo !target!>"%FAKE_MCP_STATE%.%~n0"
 exit /b 0
)
if "%2"=="remove" (del /q "%FAKE_MCP_STATE%.%~n0" 2>nul & exit /b 0)
exit /b 0
'@
[IO.File]::WriteAllText((Join-Path $fake 'codex.cmd'),$fakeTemplate);[IO.File]::WriteAllText((Join-Path $fake 'claude.cmd'),$fakeTemplate)
$oldPath=$env:PATH;$env:PATH="$fake;$oldPath";$env:FAKE_MCP_LOG=Join-Path $base 'mcp.log';$env:FAKE_MCP_STATE=Join-Path $base 'mcp.state'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex
Assert ($LASTEXITCODE -eq 0) 'both client registrations are added and verified';$clientLog=Get-Content -Raw $env:FAKE_MCP_LOG;Assert ($clientLog -like '*--env*FINDFAST_DATA_DIR=*') 'fake CLI receives environment argument';Assert ($clientLog -like '*--transport stdio*--scope user*') 'Claude command uses official transport/scope flags';Assert ($clientLog -like "*$install*") 'installed executable path passed as one quoted argument'
# The name must sit immediately after 'add' and '--' must close the variadic --env list: commander
# swallows every following non-option token into --env, which previously left commandOrUrl missing.
Assert ($clientLog -like '*mcp add findfast --transport stdio --scope user --env *FINDFAST_DATA_DIR=* -- *') 'Claude add places the server name before options and closes --env with --'
$installedExe=Join-Path $install 'FindFast.Server.exe';[IO.File]::WriteAllText("$env:FAKE_MCP_STATE.codex",$installedExe);[IO.File]::WriteAllText("$env:FAKE_MCP_STATE.claude",$installedExe)
$addsBefore=([regex]::Matches($clientLog,'mcp add')).Count
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex
$addsAfter=([regex]::Matches((Get-Content -Raw $env:FAKE_MCP_LOG),'mcp add')).Count;Assert ($addsAfter -eq $addsBefore) 'client registration parses escaped Codex JSON and Claude output'
$divergent=Join-Path $base 'other app\FindFast.Server.exe';[IO.File]::WriteAllText("$env:FAKE_MCP_STATE.codex",$divergent);$beforeConflict=Get-Content -Raw $env:FAKE_MCP_LOG
$conflictOut=& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex 2>&1|Out-String
Assert ($conflictOut -like '*CONFLITO*codex*preservado*') 'divergent registration is preserved and summarized';Assert ((Get-Content -Raw $env:FAKE_MCP_LOG) -notlike "$beforeConflict*mcp remove findfast*") 'conflict does not remove without consent'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex -UpdateClientConflicts
$afterOptIn=Get-Content -Raw $env:FAKE_MCP_LOG;Assert ($afterOptIn -like '*mcp remove findfast*') 'opt-in conflict update removes old registration';Assert (([regex]::Matches($afterOptIn,'mcp add')).Count -gt $addsAfter) 'opt-in conflict update adds replacement'
Remove-Item "$env:FAKE_MCP_STATE.codex","$env:FAKE_MCP_STATE.claude" -Force -ErrorAction SilentlyContinue;$env:FAKE_MCP_FAIL='1';$policyOut=& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Headless -PayloadDirectory $payload -InstallDirectory $install -DataDirectory $data -ConfigurationFile $config -SkipIndex 2>&1|Out-String;Assert ($LASTEXITCODE -eq 2) 'client policy failure returns partial-success';Assert ($policyOut -like '*Falha registrando codex*' -and $policyOut -like '*Falha registrando claude*') 'client policy failures are explicit in summary';Remove-Item Env:FAKE_MCP_FAIL
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
