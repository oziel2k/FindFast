[CmdletBinding()] param([string]$OutputDirectory)
$ErrorActionPreference='Stop'; if(!$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'artifacts'}; $payload=Join-Path $PSScriptRoot 'payload'; New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
dotnet publish (Join-Path $PSScriptRoot '..\src\FindFast.Server\FindFast.Server.csproj') -c Release -r win-x64 --self-contained true -o $payload
Copy-Item (Join-Path $PSScriptRoot 'Install-FindFast.ps1'),(Join-Path $PSScriptRoot 'Uninstall-FindFast.ps1') -Destination $payload -Force
$zip=Join-Path $OutputDirectory 'FindFast-win-x64.zip'; Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force
$iscc=Get-Command iscc.exe -ErrorAction SilentlyContinue;if(!$iscc){$defaultIscc=Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe';if(Test-Path $defaultIscc){$iscc=Get-Item $defaultIscc}};if($iscc){& $iscc.FullName (Join-Path $PSScriptRoot 'FindFast.iss');if($LASTEXITCODE-ne 0){exit $LASTEXITCODE}}else{Write-Warning 'Inno Setup não encontrado; ZIP/bootstrap gerado.'}
Write-Host "Artefato: $zip"
