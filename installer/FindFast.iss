#define AppName "FindFast"
#define AppVersion "1.0.1"
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
; A página de pastas mostra oito campos e a de extensões um memo; o wizard padrão é estreito
; demais para ambos. O layout do código ainda se ajusta sozinho a qualquer tamanho.
WizardSizePercent=140
[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Run]
Filename: "powershell.exe"; Parameters: "{code:BootstrapParameters}"; Flags: runhidden waituntilterminated
[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-FindFast.ps1"" -InstallDirectory ""{app}"" -FilesManagedByInstaller"; RunOnceId: "FindFastCleanup"
[Icons]
Name: "{group}\Desinstalar FindFast"; Filename: "{uninstallexe}"
[Code]
const
  // Quantidade de campos de pasta oferecidos pelo assistente. Para aumentar, basta ampliar os
  // arrays abaixo: todo laço é limitado por esta constante e o layout se adapta à altura da página.
  RootFieldCount = 8;

var
  RootsPage: TWizardPage;
  RootEdits: array[0..7] of TNewEdit;
  RootBrowseButtons: array[0..7] of TNewButton;
  ExtensionsPage: TWizardPage;
  ExtensionsMemo: TNewMemo;

// Precisa espelhar FindFastService.DefaultExtensions. Os testes do instalador comparam as duas
// listas e falham quando o servidor ganha ou perde uma extensão sem esta sugestão ser atualizada.
function DefaultExtensionsText: String;
begin
  Result :=
    '.adoc, .astro, .bash, .bat, .bicep, .c, .cc, .cfg, .clj, .cljs, .cmake, .cmd, .conf, .cpp, ' +
    '.cs, .csproj, .css, .csv, .cxx, .dart, .dockerfile, .env, .erl, .ex, .exs, .f, .f90, .f95, ' +
    '.fpw, .fr2, .fs, .fsx, .gitignore, .go, .gql, .gradle, .graphql, .groovy, .h, .hpp, .hrl, ' +
    '.hs, .htm, .html, .ini, .java, .jl, .jrxml, .js, .json, .jsonc, .jsx, .kt, .kts, .lb2, ' +
    '.less, .lua, .m, .markdown, .md, .mdx, .mjs, .mk, .mm, .mn2, .php, .pj2, .pl, .pm, .prg, ' +
    '.properties, .proto, .ps1, .psd1, .psm1, .pxd, .pxi, .py, .pyf, .pyi, .pyx, .r, .rb, .rs, ' +
    '.rst, .sass, .sc2, .scala, .scss, .sh, .sln, .sql, .svelte, .swift, .tex, .tf, .tfvars, ' +
    '.toml, .ts, .tsv, .tsx, .txt, .vb, .vbproj, .vc2, .vue, .xml, .xsd, .xsl, .yaml, .yml, ' +
    '.zsh';
end;

function JsonEscape(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function IsExtensionSeparator(Ch: Char): Boolean;
begin
  Result := (Ch = ',') or (Ch = ';') or (Ch = ' ') or (Ch = #9) or (Ch = #13) or (Ch = #10);
end;

// Divide uma lista livre de extensões no corpo de um array JSON. Os tokens são validados aqui
// para que um valor inválido seja relatado no assistente, e não aborte o bootstrap depois de os
// arquivos já terem sido copiados. Aceita vírgula, ponto e vírgula, espaço e quebra de linha.
function TokensJson(Raw: String; var Invalid: String; var HasStar: Boolean): String;
var
  Token: String;
  I, J, Count: Integer;
  Ch: Char;
  Ok: Boolean;
begin
  Result := '';
  Invalid := '';
  HasStar := False;
  Count := 0;
  Token := '';
  Raw := Raw + ',';
  for I := 1 to Length(Raw) do
  begin
    Ch := Raw[I];
    if not IsExtensionSeparator(Ch) then
    begin
      Token := Token + Ch;
      Continue;
    end;
    if Token = '' then Continue;
    if Token = '*' then HasStar := True
    else
    begin
      if Copy(Token, 1, 1) = '.' then Token := Copy(Token, 2, Length(Token));
      Ok := Length(Token) > 0;
      for J := 1 to Length(Token) do
      begin
        Ch := Token[J];
        if not (((Ch >= 'a') and (Ch <= 'z')) or ((Ch >= 'A') and (Ch <= 'Z')) or
                ((Ch >= '0') and (Ch <= '9')) or (Ch = '_') or (Ch = '-')) then Ok := False;
      end;
      if not Ok then
      begin
        Invalid := Token;
        Result := '';
        Exit;
      end;
      if Count > 0 then Result := Result + ',';
      Result := Result + '"' + JsonEscape(Token) + '"';
      Count := Count + 1;
    end;
    Token := '';
  end;
end;

// No servidor, '*' prevalece sobre qualquer outro token, então ele é emitido sozinho. Uma lista
// deixada exatamente como sugerida colapsa para [], o que mantém a raiz acompanhando o conjunto
// padrão do servidor em vez de congelar o retrato de hoje.
function ExtensionsJson(var Invalid: String): String;
var
  Body, DefaultBody, DefaultInvalid: String;
  HasStar, DefaultHasStar: Boolean;
begin
  Body := TokensJson(ExtensionsMemo.Text, Invalid, HasStar);
  if Invalid <> '' then
  begin
    Result := '';
    Exit;
  end;
  if HasStar then
  begin
    Result := '"*"';
    Exit;
  end;
  DefaultBody := TokensJson(DefaultExtensionsText, DefaultInvalid, DefaultHasStar);
  if Body = DefaultBody then Result := '' else Result := Body;
end;

procedure RootBrowseClick(Sender: TObject);
var
  I: Integer;
  Directory: String;
begin
  for I := 0 to RootFieldCount - 1 do
    if Sender = RootBrowseButtons[I] then
    begin
      Directory := Trim(RootEdits[I].Text);
      if BrowseForFolder('Selecione uma pasta para o FindFast indexar:', Directory, False) then
        RootEdits[I].Text := Directory;
      Exit;
    end;
end;

procedure CreateRootsPage;
var
  Hint, Prompt: TNewStaticText;
  I, RowTop, LabelWidth, ButtonWidth, HintHeight, RowHeight, Pitch: Integer;
begin
  RootsPage := CreateCustomPage(wpSelectDir,
    'Pastas para monitorar',
    'Selecione as pastas que o FindFast deverá indexar.');
  HintHeight := ScaleY(28);
  Hint := TNewStaticText.Create(RootsPage);
  Hint.Parent := RootsPage.Surface;
  Hint.Left := 0;
  Hint.Top := 0;
  Hint.Width := RootsPage.SurfaceWidth;
  Hint.AutoSize := False;
  Hint.WordWrap := True;
  Hint.Height := HintHeight;
  Hint.Caption := 'Você pode cadastrar até ' + IntToStr(RootFieldCount) +
    ' pastas agora. Campos vazios são ignorados; outras pastas podem ser cadastradas depois com root_add.';
  LabelWidth := ScaleX(52);
  ButtonWidth := ScaleX(75);
  // O passo entre as linhas vem da altura real da página, então nenhum valor de RootFieldCount
  // consegue transbordar a superfície: (N-1)*Pitch + RowHeight <= N*Pitch <= espaço disponível.
  RowHeight := ScaleY(23);
  Pitch := (RootsPage.SurfaceHeight - HintHeight - ScaleY(8)) div RootFieldCount;
  if Pitch > ScaleY(30) then Pitch := ScaleY(30);
  if RowHeight > Pitch then RowHeight := Pitch;
  for I := 0 to RootFieldCount - 1 do
  begin
    RowTop := HintHeight + ScaleY(8) + I * Pitch;
    Prompt := TNewStaticText.Create(RootsPage);
    Prompt.Parent := RootsPage.Surface;
    Prompt.Left := 0;
    Prompt.Top := RowTop + ScaleY(5);
    Prompt.Width := LabelWidth;
    Prompt.Caption := 'Pasta ' + IntToStr(I + 1) + ':';
    RootEdits[I] := TNewEdit.Create(RootsPage);
    RootEdits[I].Parent := RootsPage.Surface;
    RootEdits[I].Left := LabelWidth;
    RootEdits[I].Top := RowTop;
    RootEdits[I].Width := RootsPage.SurfaceWidth - LabelWidth - ButtonWidth - ScaleX(6);
    RootEdits[I].Height := RowHeight;
    RootBrowseButtons[I] := TNewButton.Create(RootsPage);
    RootBrowseButtons[I].Parent := RootsPage.Surface;
    RootBrowseButtons[I].Left := RootsPage.SurfaceWidth - ButtonWidth;
    RootBrowseButtons[I].Top := RowTop;
    RootBrowseButtons[I].Width := ButtonWidth;
    RootBrowseButtons[I].Height := RowHeight;
    RootBrowseButtons[I].Caption := 'Procurar...';
    RootBrowseButtons[I].OnClick := @RootBrowseClick;
  end;
end;

procedure ExtensionsResetClick(Sender: TObject);
begin
  ExtensionsMemo.Text := DefaultExtensionsText;
end;

procedure ExtensionsAllClick(Sender: TObject);
begin
  ExtensionsMemo.Text := '*';
end;

procedure ExtensionsClearClick(Sender: TObject);
begin
  ExtensionsMemo.Text := '';
end;

procedure CreateExtensionsPage;
var
  Hint: TNewStaticText;
  ResetButton, AllButton, ClearButton: TNewButton;
  ButtonTop, ButtonHeight, MemoTop: Integer;
begin
  ExtensionsPage := CreateCustomPage(RootsPage.ID,
    'Extensões indexadas',
    'Quais tipos de arquivo devem entrar no índice.');
  Hint := TNewStaticText.Create(ExtensionsPage);
  Hint.Parent := ExtensionsPage.Surface;
  Hint.Left := 0;
  Hint.Top := 0;
  Hint.Width := ExtensionsPage.SurfaceWidth;
  Hint.AutoSize := False;
  Hint.WordWrap := True;
  Hint.Height := ScaleY(44);
  Hint.Caption := 'A lista abaixo traz todas as extensões suportadas hoje. Remova o que não ' +
    'interessa e acrescente o que faltar, separando por vírgula. Campo vazio aplica o conjunto ' +
    'padrão do servidor; * indexa todo arquivo de texto, inclusive sem extensão, e prevalece ' +
    'sobre os demais itens.';
  MemoTop := ScaleY(50);
  ButtonHeight := ScaleY(23);
  ButtonTop := ExtensionsPage.SurfaceHeight - ButtonHeight;
  ExtensionsMemo := TNewMemo.Create(ExtensionsPage);
  ExtensionsMemo.Parent := ExtensionsPage.Surface;
  ExtensionsMemo.Left := 0;
  ExtensionsMemo.Top := MemoTop;
  ExtensionsMemo.Width := ExtensionsPage.SurfaceWidth;
  ExtensionsMemo.Height := ButtonTop - MemoTop - ScaleY(8);
  ExtensionsMemo.ScrollBars := ssVertical;
  ExtensionsMemo.Text := DefaultExtensionsText;
  ResetButton := TNewButton.Create(ExtensionsPage);
  ResetButton.Parent := ExtensionsPage.Surface;
  ResetButton.Left := 0;
  ResetButton.Top := ButtonTop;
  ResetButton.Width := ScaleX(110);
  ResetButton.Height := ButtonHeight;
  ResetButton.Caption := 'Restaurar padrão';
  ResetButton.OnClick := @ExtensionsResetClick;
  AllButton := TNewButton.Create(ExtensionsPage);
  AllButton.Parent := ExtensionsPage.Surface;
  AllButton.Left := ScaleX(116);
  AllButton.Top := ButtonTop;
  AllButton.Width := ScaleX(90);
  AllButton.Height := ButtonHeight;
  AllButton.Caption := 'Todas (*)';
  AllButton.OnClick := @ExtensionsAllClick;
  ClearButton := TNewButton.Create(ExtensionsPage);
  ClearButton.Parent := ExtensionsPage.Surface;
  ClearButton.Left := ScaleX(212);
  ClearButton.Top := ButtonTop;
  ClearButton.Width := ScaleX(90);
  ClearButton.Height := ButtonHeight;
  ClearButton.Caption := 'Limpar';
  ClearButton.OnClick := @ExtensionsClearClick;
end;

procedure InitializeWizard;
begin
  CreateRootsPage;
  CreateExtensionsPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = RootsPage.ID) or (PageID = ExtensionsPage.ID)) and
    (WizardSilent or (ExpandConstant('{param:ROOTSCONFIG|}') <> ''));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I, J: Integer;
  Invalid, Current, Previous: String;
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
  for I := 0 to RootFieldCount - 1 do
  begin
    Current := Trim(RootEdits[I].Text);
    if Current = '' then Continue;
    if not DirExists(Current) then
    begin
      MsgBox('A pasta não existe: ' + Current, mbError, MB_OK);
      Result := False;
      Exit;
    end;
    // Dois campos apontando para a mesma pasta colapsariam em uma única entrada do catálogo.
    for J := 0 to I - 1 do
    begin
      Previous := Trim(RootEdits[J].Text);
      if (Previous <> '') and
         (CompareText(RemoveBackslashUnlessRoot(Current), RemoveBackslashUnlessRoot(Previous)) = 0) then
      begin
        MsgBox('A pasta foi informada mais de uma vez: ' + Current, mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end;
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
  for I := 0 to RootFieldCount - 1 do
  begin
    Value := Trim(RootEdits[I].Text);
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
