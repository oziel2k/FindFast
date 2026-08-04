# Instalador Windows do FindFast

O instalador usa um bootstrap PowerShell transacional como fonte única da lógica e, quando disponível, Inno Setup como invólucro gráfico. A publicação é `win-x64`, autocontida e por usuário.

## Gerar o pacote

```powershell
.\installer\build-installer.ps1
```

O script publica `src/FindFast.Server` e gera `installer/artifacts/FindFast-win-x64.zip`. Se `iscc.exe` estiver no `PATH`, também gera `FindFast-Setup-win-x64.exe`. Sem Inno Setup, o ZIP é o artefato instalável suportado.

O build também procura o ISCC em `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.

## Instalação headless

Crie uma configuração:

```json
{
  "roots": [
    {
      "path": "E:\\THEAGENTSCAST",
      "name": "THEAGENTSCAST",
      "extensions": [".py", ".md", ".json"],
      "include": [],
      "exclude": ["tmp/**"],
      "respect_gitignore": true
    }
  ]
}
```

Execute:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-FindFast.ps1 `
  -Headless -ConfigurationFile .\install.json
```

Defaults:

- binários: `%LOCALAPPDATA%\Programs\FindFast`;
- dados: `%LOCALAPPDATA%\FindFast`;
- dados e índices existentes são mesclados/preservados em upgrade;
- raízes novas ou alteradas recebem `index_update full`;
- timeout de indexação: 900 segundos por raiz.

Use `-SkipIndex` para postergar índice, `-SkipClientRegistration` para não tocar nos clientes e `-UpdateClientConflicts` somente com consentimento explícito. Códigos: `0` sucesso, `1` falha antes da conclusão, `2` instalação concluída com falha parcial de indexação.

### Setup Inno silencioso

O Setup sempre executa o bootstrap; em modo silencioso ele usa o modo headless. Parâmetros:

```powershell
FindFast-Setup-win-x64.exe /VERYSILENT `
  /ROOTSCONFIG="C:\config\findfast-roots.json" `
  /DATADIR="C:\ProgramData\FindFast" `
  /SKIPINDEX=1 `
  /SKIPCLIENTS=1
```

`/UPDATECLIENTCONFLICTS=1` autoriza substituir entradas MCP divergentes. Sem essa opção, conflitos são preservados e relatados. O Inno é dono da pasta de binários; o bootstrap não copia nem faz backup redundante quando chamado pelo Setup.

## Registro MCP

O bootstrap detecta os executáveis e usa as CLIs, nunca edita seus arquivos diretamente:

```powershell
codex mcp add findfast --env FINDFAST_DATA_DIR=<data> -- <FindFast.Server.exe>
codex mcp get findfast --json

claude mcp add --transport stdio --scope user --env FINDFAST_DATA_DIR=<data> findfast -- <FindFast.Server.exe>
claude mcp get findfast
```

Para Codex, a saída JSON é parseada estruturalmente e o campo `command` é comparado por path canônico (inclusive JSON com barras escapadas). Para Claude, a saída de `get` é normalizada e o executável é extraído antes da comparação. Entrada existente que já aponta para o executável é mantida. Entrada divergente é relatada como conflito; só é substituída com `-UpdateClientConflicts`. Ausência da CLI ou falha de política não aborta a instalação e aparece no resumo/log. Consulte os manuais oficiais: [Codex MCP](https://learn.chatgpt.com/docs/extend/mcp.md) e [Claude Code MCP](https://code.claude.com/docs/en/mcp).

## Desinstalação

```powershell
.\Uninstall-FindFast.ps1
```

Por padrão remove somente binários e preserva `%LOCALAPPDATA%\FindFast`. `-RemoveData` apaga catálogo/índices, nunca raízes-fonte. `-RemoveClientRegistrations` remove a entrada somente se a consulta da CLI ainda apontar para esta instalação. No Setup, o Inno executa o script apenas para limpeza opcional e depois remove os binários; o script não tenta apagar a própria pasta. Um atalho “Desinstalar FindFast” é criado no menu Iniciar.

## Testes seguros

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\tests\Installer.Tests.ps1
```

Os testes usam somente `%TEMP%`, payload falso e não executam registro real de Codex/Claude.
