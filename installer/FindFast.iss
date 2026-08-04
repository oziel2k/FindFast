#define AppName "FindFast"
#define AppVersion "0.1.0"
[Setup]
AppId={{B9F5A11E-463A-45FD-9CB7-78686C077305}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\FindFast
PrivilegesRequired=lowest
OutputDir=artifacts
OutputBaseFilename=FindFast-Setup-win-x64
Compression=lzma2
SolidCompression=yes
Uninstallable=yes
[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Run]
Filename: "powershell.exe"; Parameters: "{code:BootstrapParameters}"; Flags: runhidden waituntilterminated
[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-FindFast.ps1"" -InstallDirectory ""{app}"" -FilesManagedByInstaller"; RunOnceId: "FindFastCleanup"
[Icons]
Name: "{group}\Desinstalar FindFast"; Filename: "{uninstallexe}"
[Code]
function BootstrapParameters(Param: String): String;
var RootConfig, DataDir: String;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install-FindFast.ps1') + '" -PayloadDirectory "' + ExpandConstant('{app}') + '" -InstallDirectory "' + ExpandConstant('{app}') + '" -FilesAlreadyInstalled';
  if WizardSilent then Result := Result + ' -Headless';
  RootConfig := ExpandConstant('{param:ROOTSCONFIG|}');
  if RootConfig <> '' then Result := Result + ' -ConfigurationFile "' + RootConfig + '"';
  DataDir := ExpandConstant('{param:DATADIR|}');
  if DataDir <> '' then Result := Result + ' -DataDirectory "' + DataDir + '"';
  if ExpandConstant('{param:SKIPINDEX|0}') = '1' then Result := Result + ' -SkipIndex';
  if ExpandConstant('{param:SKIPCLIENTS|0}') = '1' then Result := Result + ' -SkipClientRegistration';
  if ExpandConstant('{param:UPDATECLIENTCONFLICTS|0}') = '1' then Result := Result + ' -UpdateClientConflicts';
end;
