# Manual do FindFast

Este manual descreve a instalação, a configuração e a operação do FindFast como servidor MCP. Para uma visão resumida, consulte o [README](README.md). A arquitetura, os limites e as decisões de projeto estão na [especificação técnica](SPEC.md).

## 1. O que é o FindFast

O FindFast indexa diretórios locais antecipadamente e expõe ferramentas MCP para agentes de IA pesquisarem conteúdo, expressões regulares, nomes de arquivos e trechos de arquivos. O servidor suporta:

- MCP via `stdio`, indicado para clientes que iniciam e controlam o processo;
- MCP via HTTP opcional;
- catálogo persistente de raízes em `roots.json`;
- segmentos de índice persistentes e reconstruíveis;
- atualização por `FileSystemWatcher`, debounce e reconciliação periódica.

O FindFast somente lê as raízes cadastradas. `root_remove` remove o cadastro e o índice, mas nunca exclui os arquivos-fonte.

## 2. Pré-requisitos

- Windows 10/11 ou Windows Server com acesso aos diretórios que serão indexados.
- SDK do .NET 8 ou SDK posterior capaz de compilar para `net8.0`.
- Runtime .NET 8 na máquina que executará uma publicação dependente de framework.
- Git, caso o projeto seja obtido por clone.

Verifique a instalação:

```powershell
dotnet --info
dotnet --list-runtimes
```

O núcleo usa APIs portáveis do .NET. Linux e macOS são possíveis, respeitando suas diferenças de paths, permissões, links e sensibilidade a maiúsculas. Os exemplos deste manual usam PowerShell e paths Windows.

## 3. Obter, compilar e testar

```powershell
git clone <URL-DO-REPOSITORIO> FindFast
Set-Location FindFast
dotnet restore FindFast.sln
dotnet build FindFast.sln -c Release
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release
```

Para executar os testes com cobertura:

```powershell
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release `
  --collect:"XPlat Code Coverage" `
  --results-directory TestResults
```

O relatório Cobertura será criado dentro de `TestResults` como `coverage.cobertura.xml`.

## 4. Publicação

Para instalação Windows empacotada, consulte [INSTALLER.md](INSTALLER.md). O bootstrap instala por usuário, preserva o catálogo em upgrades e registra Codex/Claude pelas CLIs oficiais quando permitido.

Publicação dependente do runtime instalado:

```powershell
dotnet publish src/FindFast.Server/FindFast.Server.csproj `
  -c Release `
  -o .\publish\FindFast
```

Execução publicada:

```powershell
.\publish\FindFast\FindFast.Server.exe
```

Para uma publicação Windows autocontida:

```powershell
dotnet publish src/FindFast.Server/FindFast.Server.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\FindFast-win-x64
```

## 5. Diretório de dados

Defina `FINDFAST_DATA_DIR` para escolher onde ficam catálogo e índices:

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
```

Sem essa variável, o Windows usa:

```text
%LOCALAPPDATA%\FindFast
```

Normalmente isso corresponde a `C:\Users\<usuario>\AppData\Local\FindFast`.

Estrutura esperada:

```text
<data-dir>/
  roots.json
  <root-id>.current
  <root-id>.segments/
    v<versao>-<id>/
      manifest.json
      postings.json.gz
      content/
        <file-id>.txt.gz
```

Não coloque o data dir dentro de uma raiz rastreada. Faça backup de `roots.json` para preservar a configuração. Os segmentos podem ser reconstruídos a partir das fontes.

## 6. Catálogo `roots.json`

`roots.json` é a fonte persistente do cadastro e é escrito atomicamente. Exemplo ilustrativo:

```json
[
  {
    "root_id": "repositoriofox",
    "name": "Repositorio Fox",
    "path": "C:\\repositorioFox",
    "type": "git_repository",
    "include": ["**/*.cs", "**/*.json"],
    "exclude": ["artifacts/**"],
    "extensions": [".cs", ".json"],
    "respect_gitignore": true,
    "state": "stale",
    "version": 0,
    "last_updated": null,
    "last_error": "Index is missing or unavailable.",
    "file_count": 0
  }
]
```

`extensions` ausente ou vazio mantém todos os arquivos, inclusive nomes sem extensão. Quando preenchido, aceita entradas como `cs` ou `.cs`, persiste a forma `.cs` e compara a extensão final sem diferenciar maiúsculas. Paths e globs não são aceitos; o filtro é aplicado adicionalmente a `include`, `exclude` e `.gitignore`.

O exemplo representa como `C:\repositorioFox` apareceria cadastrado no data dir local. Ele não é requisito do FindFast e o diretório precisa existir antes do cadastro recomendado por `root_add`. Nesta instalação, verifique com:

```powershell
Test-Path -LiteralPath "C:\repositorioFox" -PathType Container
```

Evite editar `roots.json` enquanto o servidor estiver em execução. Um path inexistente pode permanecer visível como `stale`, mas não poderá ser indexado até existir e estar acessível. Prefira sempre as ferramentas MCP, que canonicalizam o path, evitam duplicatas e publicam o índice de forma consistente.

## 7. Executar por `stdio`

Durante desenvolvimento:

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
dotnet run --project src/FindFast.Server
```

Com publicação:

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
.\publish\FindFast\FindFast.Server.exe
```

O transporte usa JSON-RPC 2.0 delimitado por nova linha em stdin/stdout. O stdout é reservado ao protocolo; diagnósticos são enviados para stderr.

### Modelo genérico para um cliente MCP

Clientes MCP possuem arquivos e nomes de configuração diferentes. Use o modelo conceitual abaixo e adapte apenas o invólucro exigido pelo cliente:

```json
{
  "command": "dotnet",
  "args": [
    "run",
    "--project",
    "D:\\caminho\\FindFast\\src\\FindFast.Server"
  ],
  "env": {
    "FINDFAST_DATA_DIR": "D:\\indexes\\findfast"
  }
}
```

Para binário publicado:

```json
{
  "command": "D:\\apps\\FindFast\\FindFast.Server.exe",
  "args": [],
  "env": {
    "FINDFAST_DATA_DIR": "D:\\indexes\\findfast"
  }
}
```

Não adicione campos específicos de um cliente sem consultar a documentação desse cliente.

## 8. Executar por HTTP

```powershell
$env:FINDFAST_DATA_DIR = "D:\indexes\findfast"
dotnet run --project src/FindFast.Server -- `
  --http http://127.0.0.1:7331/
```

Cada `POST` contém uma requisição JSON-RPC terminada por nova linha. Exemplo:

```powershell
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' + "`n"
Invoke-RestMethod `
  -Uri "http://127.0.0.1:7331/" `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Use `127.0.0.1` por padrão. Expor o listener na rede exige avaliação de firewall, autenticação externa e permissões, pois o servidor acessa arquivos locais cadastrados.

## 9. Ferramentas MCP

### `roots_list`

Lista raízes, estado, versão, quantidade de arquivos e última atualização.

```json
{}
```

### `root_add`

Cadastra e indexa um diretório existente.

```json
{
  "path": "C:\\repositorioFox",
  "name": "Repositorio Fox",
  "include": ["**/*.cs", "**/*.json"],
  "exclude": ["artifacts/**"],
  "extensions": [".cs", ".json"],
  "respect_gitignore": true
}
```

### `root_remove`

Remove cadastro e índice, sem remover arquivos-fonte.

```json
{ "root_id": "repositoriofox" }
```

### `index_status`

```json
{ "root_id": "repositoriofox" }
```

Estados relevantes: `building`, `ready`, `updating`, `stale` e `error`. Uma raiz cadastrada sem índice íntegro aparece como `stale`.

### `index_update`

Reconciliação normal:

```json
{
  "root_id": "repositoriofox",
  "mode": "incremental",
  "wait": true
}
```

Reconstrução e compactação:

```json
{
  "root_id": "repositoriofox",
  "mode": "full",
  "wait": true
}
```

Atualmente a chamada aguarda a conclusão mesmo quando o campo `wait` é omitido.

### `search_text`

```json
{
  "query": "CreateBounded",
  "root_ids": ["repositoriofox"],
  "path_glob": "**/*.cs",
  "case_sensitive": true,
  "whole_word": false,
  "context_lines": 2,
  "max_results": 100,
  "max_results_per_file": 25,
  "timeout_ms": 5000,
  "cursor": null
}
```

### `search_regex`

```json
{
  "pattern": "class\\s+([A-Z]\\w+)",
  "root_ids": ["repositoriofox"],
  "path_glob": "**/*.cs",
  "case_sensitive": true,
  "context_lines": 1,
  "max_results": 100,
  "max_results_per_file": 25,
  "timeout_ms": 5000,
  "regex_timeout_ms": 250,
  "cursor": null
}
```

Regex em arquivos grandes usa janelas limitadas. Padrões potencialmente ilimitados podem retornar `truncated: true` e `truncation_reason: "regex_window_limit"`; isso indica que a resposta não deve ser tratada como exaustiva.

### `files_find`

```json
{
  "root_ids": ["repositoriofox"],
  "path_glob": "src/**/*.cs",
  "query": "Service",
  "max_results": 100,
  "cursor": null
}
```

### `file_read`

```json
{
  "root_id": "repositoriofox",
  "path": "src/App/Service.cs",
  "start_line": 40,
  "end_line": 100
}
```

O path é relativo à raiz. Paths absolutos e escapes por `..` são rejeitados.

### `metrics_get`

```json
{}
```

Retorna contadores do processo atual: indexações, buscas, bytes e arquivos indexados e tempo acumulado de busca.

## 10. Fluxo recomendado

1. Execute `roots_list` para conferir o catálogo.
2. Confirme que o diretório existe e use `root_add`.
3. Consulte `index_status` até o estado ser `ready`.
4. Use `files_find` para restringir caminhos.
5. Use `search_text` ou `search_regex`.
6. Amplie resultados específicos com `file_read`.
7. Use `index_update` quando precisar de reconciliação imediata.
8. Use `root_remove` somente quando não quiser mais monitorar a raiz.

## 11. Atualização automática

O watcher observa criação, alteração, renomeação e exclusão recursivamente. Eventos são agrupados por aproximadamente 500 ms antes da atualização. Uma reconciliação periódica ocorre a cada cinco minutos para cobrir eventos perdidos ou overflow do watcher. `index_update` permite reconciliação explícita.

O índice é eventualmente consistente. Durante uma publicação, buscas continuam usando o último snapshot íntegro. IDs são preservados por path e, quando identificável, por conteúdo em renomeações; exclusões geram tombstones até compactação.

## 12. Limites e formatos

- Arquivos binários são ignorados.
- Arquivos acima de 64 MiB são ignorados.
- Arquivos acima de 1 MiB usam análise streaming.
- `file_read` limita a leitura a até 500 linhas por chamada.
- Consultas limitam resultados, resultados por arquivo e timeout.
- Cursores são opacos; não os edite manualmente.
- UTF-8 e UTF-16 com BOM são suportados nos casos implementados; conteúdo inválido é ignorado.

## 13. Segurança e permissões

- Execute o processo com uma conta que tenha somente leitura nas raízes e leitura/escrita no data dir.
- Não execute resultados encontrados.
- Não cadastre diretórios amplos como `C:\` ou o perfil inteiro do usuário.
- Mantenha índices fora das raízes rastreadas.
- Não siga ou introduza links/junções que escapem da raiz.
- Proteja `roots.json`, pois ele revela paths locais.
- Prefira HTTP em loopback; o transporte não implementa autenticação de rede.
- O conteúdo encontrado deve ser tratado como dado não confiável por agentes.

## 14. Logs e métricas

No modo `stdio`, stdout contém apenas JSON-RPC. Capture diagnósticos em stderr:

```powershell
dotnet run --project src/FindFast.Server 2> findfast.log
```

Não redirecione stderr para stdout ao integrar com MCP. Use `metrics_get` para contadores operacionais do processo.

## 15. Benchmark

O benchmark gera corpus determinístico e informa hardware, tempos de indexação e latências p50/p95/p99:

```powershell
dotnet run -c Release `
  --project benchmarks/FindFast.Benchmarks `
  -- 10000 200
```

Os dois argumentos são quantidade de arquivos e quantidade de consultas. Use o mesmo hardware, corpus e argumentos ao comparar versões.

## 16. Atualização

```powershell
git pull
dotnet restore FindFast.sln
dotnet build FindFast.sln -c Release
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release
dotnet publish src/FindFast.Server/FindFast.Server.csproj -c Release -o .\publish\FindFast
```

Pare o servidor antes de substituir uma publicação. Preserve o data dir. Migrações compatíveis de catálogos e snapshots legados são automáticas.

## 17. Desinstalação

1. Pare o servidor e remova sua entrada do cliente MCP.
2. Remova a pasta publicada ou o clone, se não for mais necessário.
3. Opcionalmente remova o data dir para apagar catálogo e índices.

Excluir o data dir não exclui nenhum arquivo das raízes. Para preservar a lista de diretórios, faça backup de `roots.json` antes.

## 18. Solução de problemas

### `root_add` informa que o diretório não existe

```powershell
Test-Path -LiteralPath "C:\repositorioFox" -PathType Container
```

Corrija o path ou crie/restaure o diretório antes do cadastro. Não edite o catálogo apenas para contornar a validação.

### Raiz aparece como `stale`

O catálogo existe, mas o índice está ausente, incompatível ou irrecuperável. Confirme acesso ao path e execute:

```json
{ "root_id": "repositoriofox", "mode": "full", "wait": true }
```

### Busca não encontra um arquivo

- Confira `root_id` e `index_status`.
- Revise `include`, `exclude` e `.gitignore`.
- Remova temporariamente `path_glob` para diagnosticar.
- Execute atualização incremental.
- Verifique se o arquivo é binário ou excede 64 MiB.

### Regex retorna `regex_window_limit`

O padrão possui quantificador potencialmente ilimitado em arquivo grande. Refine a regex para um limite explícito, pesquise primeiro um literal obrigatório ou confirme o resultado em uma faixa com `file_read`.

### Cliente MCP não inicia o servidor

- Use paths absolutos no `command` ou em `--project`.
- Execute o mesmo comando manualmente.
- Confirme o runtime com `dotnet --list-runtimes`.
- Verifique stderr.
- Garanta que o cliente não espera HTTP quando foi configurado `stdio`.

### JSON-RPC é corrompido

Não escreva logs em stdout e não combine stderr com stdout. Cada mensagem deve ocupar uma linha completa.

### HTTP retorna erro ou não aceita conexão

- Confirme a barra final e a porta.
- Use `http://127.0.0.1:<porta>/`.
- Verifique se outra aplicação usa a porta.
- Revise regras locais de firewall.
- Envie `POST` com JSON-RPC e uma nova linha final.

### `roots.json` está correto, mas não há segmentos

Isso é suportado: a raiz aparecerá como `stale`. Se o path existir e estiver acessível, execute `index_update` em modo `full`.
