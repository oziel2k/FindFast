[CmdletBinding()]
param(
    [string]$PayloadDirectory,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\FindFast'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'FindFast'),
    [string]$ConfigurationFile,
    [switch]$Headless,
    [switch]$SkipIndex,
    [switch]$SkipClientRegistration,
    [switch]$UpdateClientConflicts,
    [switch]$FilesAlreadyInstalled,
    [switch]$TestFailBeforeCatalogPublish,
    [int]$IndexTimeoutSeconds = 900,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $PSScriptRoot 'payload' }
if (-not $LogPath) { $LogPath = Join-Path $env:TEMP ("FindFast-install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date)) }
function Write-Log([string]$Message) { $line = "[{0:O}] {1}" -f (Get-Date), $Message; Add-Content -LiteralPath $LogPath -Value $line; Write-Host $Message }
function Canonical([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }
function Normalize-Extensions($Values) {
    $result = @()
    foreach ($raw in @($Values)) { $v = ([string]$raw).Trim(); if (!$v -or $v -match '[\\/\*\?\[\]]') { throw "Extensão inválida: '$raw'" }; $v=$v.TrimStart('.'); if($v -notmatch '^[A-Za-z0-9_-]+$'){throw "Extensão inválida: '$raw'"}; $result += ".$($v.ToLowerInvariant())" }
    @($result | Sort-Object -Unique)
}
function Invoke-Cli([string]$Command, [string[]]$Arguments) { $output = & $Command @Arguments 2>&1 | Out-String; [pscustomobject]@{ ExitCode=$LASTEXITCODE; Output=$output.Trim() } }
function Find-JsonCommand($Node) { if($null-eq $Node){return $null}; if($Node -is [string]){return $null}; foreach($p in $Node.PSObject.Properties){if($p.Name -eq 'command' -and $p.Value -is [string]){return $p.Value};$nested=Find-JsonCommand $p.Value;if($nested){return $nested}};return $null }
function Get-RegisteredCommand([string]$Client,$Result) {
    if($Result.ExitCode-ne 0){return $null}
    if($Client -eq 'codex'){try{$json=$Result.Output|ConvertFrom-Json;return Find-JsonCommand $json}catch{return '__UNPARSEABLE__'}}
    $match=[regex]::Match($Result.Output,'(?im)(?:command\s*[:=]\s*)?["'']?([^"''\r\n]*FindFast\.Server\.exe)["'']?');if($match.Success){return $match.Groups[1].Value.Trim()};return '__UNPARSEABLE__'
}
function Register-Client([string]$Client, [string]$ExePath) {
    $command = Get-Command $Client -ErrorAction SilentlyContinue
    if (!$command) { Write-Log "$Client não encontrado; registro postergado."; return }
    $getArgs = if($Client -eq 'codex'){@('mcp','get','findfast','--json')}else{@('mcp','get','findfast')}
    $existing = Invoke-Cli $command.Source $getArgs
    if($existing.ExitCode -eq 0) {
        $registered=Get-RegisteredCommand $Client $existing
        if($registered-ne'__UNPARSEABLE__' -and (Canonical $registered)-ieq(Canonical $ExePath)) { Write-Log "$Client já aponta para esta instalação."; return }
        if(!$UpdateClientConflicts) { Write-Log "CONFLITO: $Client já possui findfast divergente; preservado."; return }
        $removed=Invoke-Cli $command.Source @('mcp','remove','findfast'); if($removed.ExitCode -ne 0){Write-Log "Falha removendo registro divergente de ${Client}: $($removed.Output)"; return}
    }
    $args = if($Client -eq 'codex'){@('mcp','add','findfast','--env',"FINDFAST_DATA_DIR=$DataDirectory",'--',$ExePath)}else{@('mcp','add','--transport','stdio','--scope','user','--env',"FINDFAST_DATA_DIR=$DataDirectory",'findfast','--',$ExePath)}
    $added=Invoke-Cli $command.Source $args; if($added.ExitCode -ne 0){Write-Log "Falha registrando ${Client}: $($added.Output)"; return}
    $verify=Invoke-Cli $command.Source $getArgs; Write-Log "$Client registro/verificação: exit=$($verify.ExitCode) $($verify.Output)"
}
function Invoke-Index([string]$ExePath,[string]$RootId) {
    $request = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index_update","arguments":{"root_id":"' + $RootId.Replace('"','') + '","mode":"full","wait":true}}}'
    $inputFile=Join-Path $env:TEMP ("findfast-index-$([guid]::NewGuid().ToString('N')).jsonl"); [IO.File]::WriteAllText($inputFile,$request+"`n",[Text.UTF8Encoding]::new($false))
    try { $psi=[Diagnostics.ProcessStartInfo]::new(); $psi.FileName=$ExePath; $psi.UseShellExecute=$false; $psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true; $psi.EnvironmentVariables['FINDFAST_DATA_DIR']=$DataDirectory; $p=[Diagnostics.Process]::Start($psi); $bytes=[Text.UTF8Encoding]::new($false).GetBytes([IO.File]::ReadAllText($inputFile)); $p.StandardInput.BaseStream.Write($bytes,0,$bytes.Length); $p.StandardInput.BaseStream.Close(); if(!$p.WaitForExit($IndexTimeoutSeconds*1000)){$p.Kill(); Write-Log "TIMEOUT indexando $RootId"; return $false}; $out=$p.StandardOutput.ReadToEnd(); $err=$p.StandardError.ReadToEnd(); Write-Log "Índice $RootId exit=$($p.ExitCode) $out $err"; return ($p.ExitCode -eq 0 -and $out -notlike '*"error"*') } finally { Remove-Item -LiteralPath $inputFile -Force -ErrorAction SilentlyContinue }
}

$backup=$null; $installedThisRun=$false
try {
    Write-Log "Iniciando instalação FindFast. install=$InstallDirectory data=$DataDirectory"
    if(!(Test-Path -LiteralPath $PayloadDirectory)){throw "Payload ausente: $PayloadDirectory"}
    $roots=@(); if($ConfigurationFile){$config=Get-Content -Raw -LiteralPath $ConfigurationFile|ConvertFrom-Json; $roots=@($config.roots)} elseif(!$Headless){$entered=Read-Host 'Diretórios para monitorar (separados por ;) ou vazio'; foreach($p in $entered.Split(';',[StringSplitOptions]::RemoveEmptyEntries)){$name=Read-Host "Nome amigável para $p (vazio=nome da pasta)";$extensions=(Read-Host 'Extensões separadas por vírgula (vazio=todas)').Split(',',[StringSplitOptions]::RemoveEmptyEntries);$include=(Read-Host 'Globs include separados por ; (vazio=todos)').Split(';',[StringSplitOptions]::RemoveEmptyEntries);$exclude=(Read-Host 'Globs exclude separados por ; (vazio=nenhum)').Split(';',[StringSplitOptions]::RemoveEmptyEntries);$git=(Read-Host 'Respeitar .gitignore? [S/n]');$roots += [pscustomobject]@{path=$p;name=$name;extensions=$extensions;include=$include;exclude=$exclude;respect_gitignore=($git -notmatch '^[Nn]')}}}
    foreach($root in $roots){if(!(Test-Path -LiteralPath $root.path -PathType Container)){throw "Raiz inexistente: $($root.path)"}}
    if(!$FilesAlreadyInstalled){$stage=Join-Path $env:TEMP ("FindFast-stage-$([guid]::NewGuid().ToString('N'))"); New-Item -ItemType Directory -Path $stage|Out-Null; Copy-Item -Path (Join-Path $PayloadDirectory '*') -Destination $stage -Recurse -Force
      if(Test-Path -LiteralPath $InstallDirectory){$backup="$InstallDirectory.backup-$([guid]::NewGuid().ToString('N'))"; Move-Item -LiteralPath $InstallDirectory -Destination $backup}
      try { New-Item -ItemType Directory -Path (Split-Path $InstallDirectory) -Force|Out-Null; Move-Item -LiteralPath $stage -Destination $InstallDirectory; $installedThisRun=$true } catch { if($backup){Move-Item -LiteralPath $backup -Destination $InstallDirectory}; throw }}
    New-Item -ItemType Directory -Path $DataDirectory -Force|Out-Null; $catalogPath=Join-Path $DataDirectory 'roots.json'; $catalog=@(); if(Test-Path -LiteralPath $catalogPath){$loadedCatalog=Get-Content -Raw -LiteralPath $catalogPath|ConvertFrom-Json; foreach($entry in @($loadedCatalog)){$catalog += $entry}}
    $changed=@(); foreach($input in $roots){$path=Canonical $input.path; $found=$catalog|Where-Object{(Canonical $_.path)-ieq $path}|Select-Object -First 1; $ext=Normalize-Extensions $input.extensions; if(!$found){$id=([IO.Path]::GetFileName($path)-replace '[^A-Za-z0-9]+','-').Trim('-').ToLowerInvariant();if(!$id){$id='root'};$base=$id;$n=2;while($catalog.root_id -contains $id){$id="$base-$n";$n++};$found=[pscustomobject]@{root_id=$id;name=if($input.name){$input.name}else{[IO.Path]::GetFileName($path)};path=$path;type=if(Test-Path (Join-Path $path '.git')){'git_repository'}else{'directory'};include=@($input.include);exclude=@($input.exclude);extensions=$ext;respect_gitignore=if($null-ne $input.respect_gitignore){[bool]$input.respect_gitignore}else{$true};state='stale';version=0;last_updated=$null;last_error='Index is missing or unavailable.';file_count=0};$catalog += $found;$changed += $found.root_id}else{$before=($found|ConvertTo-Json -Depth 8 -Compress);$found.include=@($input.include);$found.exclude=@($input.exclude);if($found.PSObject.Properties.Name -contains 'extensions'){$found.extensions=$ext}else{$found|Add-Member -NotePropertyName extensions -NotePropertyValue $ext};$found.respect_gitignore=if($null-ne $input.respect_gitignore){[bool]$input.respect_gitignore}else{$true};if(($found|ConvertTo-Json -Depth 8 -Compress)-ne $before){$found.state='stale';$changed += $found.root_id}}}
    $tmp="$catalogPath.tmp-$([guid]::NewGuid().ToString('N'))"; [IO.File]::WriteAllText($tmp,(ConvertTo-Json -InputObject @($catalog) -Depth 8),[Text.UTF8Encoding]::new($false)); if($TestFailBeforeCatalogPublish){throw 'Injected failure before catalog publication'}; Move-Item -LiteralPath $tmp -Destination $catalogPath -Force
    $exe=Join-Path $InstallDirectory 'FindFast.Server.exe'; $partial=$false; if(!$SkipIndex){foreach($id in $changed){if(!(Invoke-Index $exe $id)){$partial=$true}}}else{Write-Log 'Indexação postergada por opção.'}
    if(!$SkipClientRegistration){Register-Client 'codex' $exe; Register-Client 'claude' $exe}
    if($backup){Remove-Item -LiteralPath $backup -Recurse -Force}; Write-Log "Instalação concluída. parcial=$partial log=$LogPath"; if($partial){exit 2}else{exit 0}
} catch { Write-Log "FALHA: $($_.Exception.Message)"; if($tmp -and (Test-Path -LiteralPath $tmp)){Remove-Item -LiteralPath $tmp -Force}; if($installedThisRun -and (Test-Path -LiteralPath $InstallDirectory)){Remove-Item -LiteralPath $InstallDirectory -Recurse -Force}; if($backup -and (Test-Path -LiteralPath $backup)){Move-Item -LiteralPath $backup -Destination $InstallDirectory}; exit 1 }
