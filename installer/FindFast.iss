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
  ExtensionsPage: TInputQueryWizardPage;

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
  ExtensionsPage := CreateInputQueryPage(RootsPage.ID,
    'Extensões indexadas',
    'Quais tipos de arquivo devem entrar no índice.',
    'Separe por vírgula, por exemplo: cs, sql, md. Deixe vazio para usar o conjunto padrão do FindFast, que já cobre os formatos de código e texto mais comuns. Use * para indexar todo arquivo de texto, inclusive os sem extensão.');
  ExtensionsPage.Add('Extensões:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = RootsPage.ID) or (PageID = ExtensionsPage.ID)) and
    (WizardSilent or (ExpandConstant('{param:ROOTSCONFIG|}') <> ''));
end;

// Splits the comma-separated field into a JSON array body. Tokens are validated here so an invalid
// value is reported in the wizard instead of aborting the bootstrap after files are already copied.
function ExtensionsJson(var Invalid: String): String;
var
  Raw, Token: String;
  P, I, Count: Integer;
  Ch: Char;
  Ok: Boolean;
begin
  Result := '';
  Invalid := '';
  Count := 0;
  Raw := Trim(ExtensionsPage.Values[0]) + ',';
  while Length(Raw) > 0 do
  begin
    P := Pos(',', Raw);
    if P = 0 then Break;
    Token := Trim(Copy(Raw, 1, P - 1));
    Raw := Copy(Raw, P + 1, Length(Raw));
    if Token = '' then Continue;
    if Token <> '*' then
    begin
      if Copy(Token, 1, 1) = '.' then Token := Copy(Token, 2, Length(Token));
      Ok := Length(Token) > 0;
      for I := 1 to Length(Token) do
      begin
        Ch := Token[I];
        if not (((Ch >= 'a') and (Ch <= 'z')) or ((Ch >= 'A') and (Ch <= 'Z')) or
                ((Ch >= '0') and (Ch <= '9')) or (Ch = '_') or (Ch = '-')) then Ok := False;
      end;
      if not Ok then
      begin
        Invalid := Token;
        Result := '';
        Exit;
      end;
    end;
    if Count > 0 then Result := Result + ',';
    Result := Result + '"' + JsonEscape(Token) + '"';
    Count := Count + 1;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
  Invalid: String;
begin
  Result := True;
  if CurPageID = ExtensionsPage.ID then
  begin
    ExtensionsJson(Invalid);
    if Invalid <> '' then
    begin
      MsgBox('Extensão inválida: ' + Invalid + #13#10 + 'Use valores como cs, .cs ou * para todos.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;
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
  Json, Value, Extensions, Invalid: String;
begin
  Result := ExpandConstant('{tmp}\findfast-roots.json');
  Extensions := ExtensionsJson(Invalid);
  Json := '{"roots":[';
  Count := 0;
  for I := 0 to 2 do
  begin
    Value := Trim(RootsPage.Values[I]);
    if Value <> '' then
    begin
      if Count > 0 then Json := Json + ',';
      Json := Json + '{"path":"' + JsonEscape(Value) + '","include":[],"exclude":[],"extensions":[' + Extensions + '],"respect_gitignore":true}';
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
