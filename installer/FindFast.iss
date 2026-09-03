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
var
  RootsPage: TInputDirWizardPage;

function JsonEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure InitializeWizard;
begin
  RootsPage := CreateInputDirPage(wpSelectDir,
    'Pastas para monitorar',
    'Selecione as pastas que o FindFast deverá indexar.',
    'Você pode cadastrar até três pastas agora. Campos vazios serão ignorados; outras pastas podem ser cadastradas posteriormente.',
    False, '');
  RootsPage.Add('Pasta 1:');
  RootsPage.Add('Pasta 2:');
  RootsPage.Add('Pasta 3:');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = RootsPage.ID) and
    (WizardSilent or (ExpandConstant('{param:ROOTSCONFIG|}') <> ''));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
begin
  Result := True;
  if CurPageID <> RootsPage.ID then Exit;
  for I := 0 to 2 do
    if (Trim(RootsPage.Values[I]) <> '') and not DirExists(Trim(RootsPage.Values[I])) then
    begin
      MsgBox('A pasta não existe: ' + Trim(RootsPage.Values[I]), mbError, MB_OK);
      Result := False;
      Exit;
    end;
end;

function CreateWizardRootConfig: String;
var
  I, Count: Integer;
  Json, Value: String;
begin
  Result := ExpandConstant('{tmp}\findfast-roots.json');
  Json := '{"roots":[';
  Count := 0;
  for I := 0 to 2 do
  begin
    Value := Trim(RootsPage.Values[I]);
    if Value <> '' then
    begin
      if Count > 0 then Json := Json + ',';
      Json := Json + '{"path":"' + JsonEscape(Value) + '","include":[],"exclude":[],"extensions":[],"respect_gitignore":true}';
      Count := Count + 1;
    end;
  end;
  Json := Json + ']}';
  SaveStringToFile(Result, Json, False);
end;

function BootstrapParameters(Param: String): String;
var RootConfig, DataDir: String;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install-FindFast.ps1') + '" -PayloadDirectory "' + ExpandConstant('{app}') + '" -InstallDirectory "' + ExpandConstant('{app}') + '" -FilesAlreadyInstalled -Headless';
  RootConfig := ExpandConstant('{param:ROOTSCONFIG|}');
  if (RootConfig = '') and not WizardSilent then RootConfig := CreateWizardRootConfig;
  if RootConfig <> '' then Result := Result + ' -ConfigurationFile "' + RootConfig + '"';
  DataDir := ExpandConstant('{param:DATADIR|}');
  if DataDir <> '' then Result := Result + ' -DataDirectory "' + DataDir + '"';
  if ExpandConstant('{param:SKIPINDEX|0}') = '1' then Result := Result + ' -SkipIndex';
  if ExpandConstant('{param:SKIPCLIENTS|0}') = '1' then Result := Result + ' -SkipClientRegistration';
  if ExpandConstant('{param:UPDATECLIENTCONFLICTS|0}') = '1' then Result := Result + ' -UpdateClientConflicts';
end;
