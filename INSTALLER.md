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

Em `extensions`, uma lista vazia aplica o conjunto padrão de extensões de código e texto e `["*"]` indexa todo arquivo de texto, inclusive os sem extensão.

Execute:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-FindFast.ps1 `
  -Headless -ConfigurationFile .\install.json
```

O assistente gráfico oferece oito campos de pasta, cada um com botão **Procurar...**. Campos vazios são ignorados, pastas inexistentes e pastas repetidas são recusadas ali mesmo, antes de qualquer arquivo ser copiado. Para cadastrar mais de oito raízes, use `-ConfigurationFile` ou `root_add` depois da instalação.

A página seguinte traz a lista de extensões indexadas já preenchida com todo o conjunto suportado pelo servidor, em um campo editável: remova o que não interessa, acrescente o que faltar e separe por vírgula, ponto e vírgula, espaço ou quebra de linha. Os botões **Restaurar padrão**, **Todas (\*)** e **Limpar** cobrem os casos extremos. Uma lista deixada exatamente como sugerida é gravada como `[]`, de modo que a raiz continua acompanhando o conjunto padrão do servidor em vez de congelar o retrato do dia da instalação; campo vazio tem o mesmo efeito. O token `*` indexa todo arquivo de texto, inclusive os sem extensão, e prevalece sobre os demais itens, como no servidor. As mesmas extensões valem para todas as pastas informadas no assistente; para filtros distintos por raiz, use `-ConfigurationFile` ou `root_update` depois da instalação.

A lista sugerida é uma cópia de `FindFastService.DefaultExtensions`, e os testes do instalador comparam as duas: o servidor não pode ganhar ou perder uma extensão sem que a sugestão do assistente acompanhe.

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

claude mcp add findfast --transport stdio --scope user --env FINDFAST_DATA_DIR=<data> -- <FindFast.Server.exe>
claude mcp get findfast
```

O nome do servidor precede as opções e `--` fecha a lista antes do executável: `claude mcp add` declara `-e/--env` como opção variádica, então qualquer token não-opção depois dela é absorvido — com o nome no fim, ele era engolido pelo `--env` e a CLI respondia `missing required argument 'commandOrUrl'`.

Para Codex, a saída JSON é parseada estruturalmente e o campo `command` é comparado por path canônico (inclusive JSON com barras escapadas). Para Claude, a saída de `get` é normalizada e o executável é extraído antes da comparação. Depois de cada `add`, o instalador executa um novo `get` e somente considera o registro concluído se o destino canônico for o executável instalado. Entrada existente que já aponta para o executável é mantida. Entrada divergente é relatada como conflito; só é substituída com `-UpdateClientConflicts`. Ausência da CLI, falha de política ou falha de verificação produz o código `2` (instalação parcial) e aparece no resumo/log. Catálogos antigos com campos de coleção `null` ou `{}` são normalizados para arrays antes da inicialização do servidor. Consulte os manuais oficiais: [Codex MCP](https://learn.chatgpt.com/docs/extend/mcp.md) e [Claude Code MCP](https://code.claude.com/docs/en/mcp).

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
