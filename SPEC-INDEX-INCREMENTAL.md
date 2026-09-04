# Spec — Atualização incremental de índice

Fecha a lacuna entre `SPEC.md` §6 ("Atualização incremental") e a implementação atual, que reconstrói tudo em qualquer modo. Escopo: `FindFast.Core` (serviço, store, filtros) e uma correção de registro MCP no instalador.

## 1. Problema

Medido na instalação de 2026-09-03 (`FindFast-install-20260903-202546.log`): 3 raízes, 33k arquivos, ~9 minutos. `C:\microteste` (28k arquivos, 1,7 GB) levou ~3 min sozinho.

### 1.1 Não existe modo incremental

[`FindFastService.cs:82`](src/FindFast.Core/FindFastService.cs#L82):

```csharp
_ = full; // Current snapshot format rebuilds postings atomically for both modes.
```

O parâmetro `full` só distingue retenção de tombstones ([:137](src/FindFast.Core/FindFastService.cs#L137)). O corpo é idêntico: reenumera a árvore, relê todo byte, recalcula SHA-256, trigramas e `LineStarts`, e reconstrói o dicionário de postings do zero. Salvar um arquivo custa o mesmo que a indexação inicial.

### 1.2 Persistência reescreve todo o conteúdo por versão

[`SnapshotStore.SaveAsync:67-77`](src/FindFast.Core/SnapshotStore.cs#L67-L77) grava um blob gzip novo para **cada** arquivo do snapshot no segmento novo. Como `IndexUpdateAsync` sempre popula `SourcePath` ([:126](src/FindFast.Core/FindFastService.cs#L126)), o caminho efetivo é sempre `WriteGzipFileAsync` — recompressão integral da árvore a partir da origem. Para `microteste` isso é ~1,7 GB lidos e recomprimidos a cada atualização.

### 1.3 Watcher sem filtro

[`StartWatcher:521-531`](src/FindFast.Core/FindFastService.cs#L521-L531) assina a árvore inteira com `IncludeSubdirectories = true` e nenhum predicado. `EnumerateFiles` respeita `.gitignore`, `Exclude`, `Extensions` e `DefaultExcluded` (`.git`, `node_modules`, `bin`, `obj`, `.findfast`), mas o watcher não. Escrita em `.git/` (qualquer `git status`), `obj/` (qualquer build) ou `node_modules/` dispara reconstrução integral de arquivos que nem entram no índice. `InternalBufferSize` fica no padrão de 8 KB, propenso a overflow em árvores grandes.

### 1.4 Rebuild em andamento é cancelado e descartado

[`Debounce:533-544`](src/FindFast.Core/FindFastService.cs#L533-L544) cancela o `CancellationTokenSource` anterior. Se o rebuild anterior já passou do `Task.Delay`, o token cancelado estoura no `ThrowIfCancellationRequested` do laço de arquivos e a exceção é engolida. O snapshot só é persistido no fim — todo o trabalho parcial é perdido. Sob edição contínua numa raiz grande, o índice pode nunca convergir.

### 1.5 `IsGitIgnored` sem cache

[`IsGitIgnored:396-425`](src/FindFast.Core/FindFastService.cs#L396-L425) é chamado uma vez por arquivo e por diretório. A cada chamada ele relê do disco todo `.gitignore` da cadeia de diretórios e chama `Regex.IsMatch(input, pattern)` por padrão. A cache estática de `Regex` guarda 15 entradas por padrão; com mais padrões que isso, cada chamada recompila. É custo dominante da enumeração e inviabilizaria filtrar eventos do watcher.

### 1.6 Colisão latente de `FileId`

Na atribuição de ids ([:116-121](src/FindFast.Core/FindFastService.cs#L116-L121)), o ramo de detecção de rename por hash roda antes de o arquivo de origem ser enumerado. Se um arquivo novo `q` tem o mesmo conteúdo de um arquivo existente `p` e `q` é enumerado primeiro, `q` toma o `FileId` de `p` via `oldByHash`; quando `p` é alcançado, o ramo por caminho devolve o mesmo id sem consultar `usedIds` — dois arquivos com `FileId` idêntico. `FilesById` (`Files.ToDictionary`) lança em duplicata.

### 1.7 Compactação só no modo `full`

[`:150`](src/FindFast.Core/FindFastService.cs#L150): `if (full) _store.Compact(rootId)`. Cada atualização cria um segmento novo; sem compactação em modo incremental, os segmentos acumulam indefinidamente.

## 2. Objetivos

1. Custo de uma atualização proporcional ao que mudou, não ao tamanho da raiz.
2. Varredura de reconciliação sem alteração alguma → sem I/O de conteúdo, sem novo segmento, sem incremento de versão.
3. Eventos do watcher filtrados pelos mesmos critérios da enumeração.
4. Nenhum trabalho de indexação descartado por cancelamento.

### Não-objetivos

- Alterar o formato do segmento ou `SchemaVersion` (permanece `1`).
- Alterar o contrato MCP. `index_update` mantém `mode: incremental | full`.
- Paralelizar a indexação (`SPEC.md` §6 prevê `Channels`; fora deste escopo).

## 3. Design

### 3.1 Classificação por `stat`

`IndexUpdateAsync(rootId, full, ct)` passa a começar por uma varredura que só faz `stat`:

| Classe | Critério |
|---|---|
| **Inalterado** | `!full` e existe entrada anterior com mesmo caminho relativo, `Size` e `Modified` (`LastWriteTimeUtc`) |
| **Sujo** | enumerado e não inalterado (novo ou modificado) |
| **Removido** | presente em `previous.Files` e não enumerado |

`full: true` classifica tudo como sujo.

**Saída antecipada:** se `!full` e não há sujos nem removidos, retorna o snapshot publicado inalterado — sem gravar segmento, sem incrementar `Version`, sem tocar em `LastUpdated` nem no catálogo. É isto que torna o reconcile de 5 minutos barato.

Só arquivos sujos passam por leitura, `IsBinary`, decodificação, SHA-256, trigramas e `LineStarts`. Arquivos inalterados reutilizam o `IndexedFile` anterior, com `SourcePath` repopulado com o caminho absoluto atual (necessário como fallback de persistência, §3.3).

### 3.2 Atribuição de `FileId`

Ordem fixa, que também corrige §1.6:

1. Inalterados mantêm o id anterior; esses ids entram em `usedIds` **antes** de qualquer outra resolução.
2. Sujo com caminho correspondente em `previous` mantém o id daquele caminho.
3. Sujo sem caminho correspondente tenta rename: primeiro id não usado em `oldByHash[hash]`, restrito a entradas anteriores **removidas**.
4. Caso contrário, id novo a partir de `max(FileId ∪ Tombstones.FileId) + 1`.

Preserva o invariante de `TestStableIds` (rename mantém o id) e torna impossível a duplicata.

### 3.3 Postings incrementais com copy-on-write

O snapshot publicado é lido por buscas concorrentes sem lock. Nenhuma `List<int>` alcançável pelo snapshot anterior pode ser mutada.

```
deadIds = ids dos removidos ∪ ids dos sujos que já existiam
```

Construção do dicionário novo:

- Para cada `(trigrama, ids)` de `previous.Trigrams`: se nenhum `id ∈ deadIds`, **reaproveita a referência da lista** (sem cópia). Caso contrário, aloca lista filtrada; descarta a entrada se ficar vazia.
- Para os trigramas dos arquivos sujos: antes do primeiro `Add` em uma entrada, se a lista ainda for compartilhada com o snapshot anterior, substitui por `new List<int>(existing)` e marca como própria. Entradas filtradas ou recém-criadas já são próprias.

Sem duplicatas: `TextIndex.Trigrams` já deduplica por arquivo, e os ids sujos foram removidos antes da reinserção. A ordem dentro da lista é irrelevante — `SearchText` reordena candidatos por caminho ([:189](src/FindFast.Core/FindFastService.cs#L189)).

### 3.4 Persistência: reaproveitar blobs

Nova sobrecarga:

```csharp
public Task SaveAsync(RootSnapshot snapshot, long? reuseFromVersion,
                      IReadOnlySet<int>? reusableIds, CancellationToken ct = default)
```

A sobrecarga de 2 argumentos permanece, delegando com `null, null` (usada por `ImportLegacyAsync` e testes).

Precedência por arquivo:

1. `reusableIds` contém o `FileId`, `reuseFromVersion` tem valor e o blob existe → **hard link, com fallback para cópia**. Sem descompressão, sem recompressão.
2. `SourcePath is not null` → comprime da origem (arquivos sujos; também cobre o caso de blob ausente por segmento corrompido).
3. `Content.Length > 0` → comprime do conteúdo em memória (importação legada).
4. Fallback atual `ReadContent`.

`reusableIds` é explícito e obrigatório para o passo 1: um arquivo sujo tem blob com o mesmo `FileId` no segmento anterior, mas com conteúdo obsoleto — reaproveitar por id seria corrupção silenciosa.

Hard link via `CreateHardLinkW` (kernel32) no Windows; qualquer falha, ou outro SO, cai em `File.Copy`. Hard links são seguros com `Compact`: apagar o diretório de um segmento não remove dados ainda referenciados por outro link.

### 3.5 Compactação em toda atualização

`_store.Compact(rootId)` passa a rodar em ambos os modos, com o mesmo `retain = 2` (atual + anterior). Sem isso, §1.7 vira vazamento de disco assim que o incremental fica barato.

### 3.6 Filtro de eventos do watcher

O predicado de `EnumerateFiles` é extraído para um método estático reutilizável:

```csharp
private static bool IsIndexable(RootDefinition root, string absolutePath, bool treatAsDirectory)
```

Verificações, em ordem de custo crescente: caminho dentro da raiz → nenhum segmento em `DefaultExcluded` → `Exclude` → `Include` → `Extensions` → `RespectGitignore && IsGitIgnored`.

Aplicação por tipo de evento:

| Evento | Regra |
|---|---|
| `Created` / `Changed` / `Deleted` | agenda só se `IsIndexable` |
| `Renamed` | agenda se o caminho antigo **ou** o novo for `IsIndexable` |
| `Error` | agenda sempre — overflow de buffer significa eventos perdidos |

Em `Deleted` não há `stat` possível; o caminho é avaliado como arquivo. Um arquivo removido que não passaria nos filtros nunca esteve no índice, então ignorá-lo é correto.

`InternalBufferSize` sobe para 64 KB (máximo aceito) para reduzir overflow.

### 3.7 Cache de `.gitignore`

Cache por serviço, chaveada por diretório-base, guardando regras já compiladas:

```csharp
sealed record GitignoreRule(Regex Pattern, bool Negated);
sealed record GitignoreFile(DateTime Stamp, long Size, GitignoreRule[] Rules);
ConcurrentDictionary<string, GitignoreFile> _gitignoreCache;
```

Invalidação por `LastWriteTimeUtc` + `Length` do próprio `.gitignore`; ausência do arquivo é cacheada como conjunto vazio. Cada `Regex` é construído uma vez, com o mesmo timeout de 100 ms, eliminando o thrash da cache estática de `Regex`. A semântica de correspondência atual (âncora, `!` negado, sufixo `/`, prefixo `**/`) é preservada byte a byte.

Isto é pré-requisito de §3.1 e §3.6, não otimização opcional: a varredura por `stat` chama o predicado uma vez por caminho a cada reconcile.

### 3.8 Debounce que não descarta trabalho

Separa as duas cancelabilidades hoje fundidas num único token:

- A **janela de debounce** continua cancelável — um evento novo reinicia a espera.
- A **indexação em si** passa a rodar sob o token de desligamento do serviço (`_shutdown`), não sob o token do debounce.

Um evento chegado durante uma indexação em curso não a aborta; espera o semáforo e roda uma passada incremental adicional, que será barata. `Dispose` cancela `_shutdown`. `RootRemove` continua cancelando o debounce pendente da raiz.

Janela de 500 ms → 1000 ms. Com a varredura por `stat` custando I/O em toda a árvore, coalescer saves em rajada compensa; a margem de 3 s de `TestWatcher` continua folgada.

### 3.9 Métricas

`BytesIndexed` e `FilesIndexed` passam a contar apenas arquivos sujos — trabalho efetivo, não tamanho da raiz. `IndexOperations` incrementa só quando há publicação de versão (a saída antecipada não conta).

## 4. Correção do instalador — registro no Claude Code

Descoberta durante o diagnóstico, independente do índice. O log terminou com:

```
Falha registrando claude: error: missing required argument 'commandOrUrl'
Instalação concluída. parcial=True
```

`claude mcp get findfast` confirma: o servidor não ficou registrado.

Causa em [`Install-FindFast.ps1:65`](installer/Install-FindFast.ps1#L65):

```powershell
@('mcp','add','--transport','stdio','--scope','user','--env',"FINDFAST_DATA_DIR=$DataDirectory",'findfast','--',$ExePath)
```

`claude mcp add` declara `-e, --env <env...>` — opção **variádica**. O parser consome todos os tokens não-opção seguintes, engolindo `FINDFAST_DATA_DIR=...` **e** `findfast`. Sobram `--` e o executável, então `<name>` liga ao executável e `<commandOrUrl>` fica vazio.

Correção — nome antes das opções, conforme `claude mcp add [options] <name> <commandOrUrl> [args...]`:

```powershell
@('mcp','add','findfast','--transport','stdio','--scope','user','--env',"FINDFAST_DATA_DIR=$DataDirectory",'--',$ExePath)
```

O `--` encerra a lista variádica antes do executável. `INSTALLER.md` documenta a ordem errada e é corrigido junto.

## 5. Extensões rastreáveis

`RootDefinition.Extensions` já existia e já era aplicado em `EnumerateFiles`, mas com a semântica "vazio = indexa tudo". É por isso que `C:\microteste` entrou com 28k arquivos: lockfiles, bundles minificados, fixtures e saída gerada. Além disso não havia como mudar o filtro de uma raiz já cadastrada — só remover e recadastrar, descartando índice e `file_id`.

### 5.1 Conjunto padrão

`FindFastService.DefaultExtensions` passa a listar ~90 extensões de código, marcação, dados e configuração. A semântica de `extensions` fica com um valor por significado:

| Valor | Efeito |
|---|---|
| ausente ou `[]` | conjunto padrão |
| `["cs","sql"]` | somente essas |
| `["*"]` | todo arquivo de texto, inclusive sem extensão |

**Mudança de comportamento.** Antes, `[]` indexava tudo. Raízes já cadastradas com `[]` — incluindo as três desta instalação — passam a usar o conjunto padrão na próxima atualização. Isso é intencional: é a correção do problema. Arquivos que deixam de qualificar não são reenumerados, então a máquina incremental de §3 os trata como removidos: viram tombstones e perdem suas postings, sem rebuild. Quem depender do comportamento anterior configura `["*"]`.

Arquivos sem extensão (`LICENSE`, `Makefile`, `Dockerfile`) ficam fora do conjunto padrão. `Path.GetExtension` devolve vazio para eles e a lista é de extensões, não de nomes. Trade-off aceito: quem precisa deles usa `["*"]`.

### 5.2 `root_update`

Nova ferramenta MCP para alterar `include`, `exclude`, `extensions` e `respect_gitignore` de uma raiz existente. Campos omitidos são preservados. Depois de gravar o catálogo, dispara reconciliação **incremental** — que já cobre estreitar e alargar o filtro corretamente — mantendo `file_id` e conteúdo dos arquivos que continuam qualificando.

### 5.3 Instalador

O assistente do Inno ganha uma página de extensões (campo único, separado por vírgula, aplicado às pastas informadas), com validação dos tokens no próprio wizard para não abortar o bootstrap depois de copiar arquivos. `Normalize-Extensions` do bootstrap aceita `*`.

## 6. Compatibilidade

- `SchemaVersion` permanece `1`; segmentos existentes são lidos sem migração.
- Índices gravados antes desta mudança funcionam: na primeira atualização incremental, `Size`/`Modified` do manifesto são comparados normalmente.
- Sem alteração no contrato MCP nem no formato de `roots.json`.
- `Reused` não vira campo de modelo — a informação trafega como parâmetro de `SaveAsync`, sem tocar em `Models.cs`.

## 7. Testes

Acréscimos a `tests/FindFast.Tests/Program.cs`, no estilo existente:

| Teste | Verifica |
|---|---|
| `TestIncrementalNoChange` | sweep sem alterações mantém `Version` e não cria segmento |
| `TestIncrementalReadsOnlyDirtyFiles` | alterar 1 de 5 arquivos incrementa `FilesIndexed` em 1 |
| `TestIncrementalPreservesCarriedContent` | conteúdo carregado por link/cópia continua legível e pesquisável |
| `TestDuplicateContentKeepsDistinctIds` | arquivo novo com conteúdo idêntico não colide de `FileId` (§1.6) |
| `TestSegmentsCompactedOnIncremental` | quatro atualizações incrementais deixam 2 segmentos |
| `TestWatcherIgnoresFilteredPaths` | escrita em `obj/` e em caminho do `.gitignore` não avança a versão |
| `TestGitignoreCacheInvalidation` | editar `.gitignore` vale na atualização seguinte |
| `TestConcurrentEventDoesNotDiscardIndexing` | evento durante indexação não aborta a passada em curso |
| `TestEmptyExtensionsUseDefaultSetAndPersist` | `[]` aplica o conjunto padrão e persiste como `[]` |
| `TestAllExtensionsToken` | `["*"]` indexa inclusive arquivo sem extensão |
| `TestRootUpdateChangesExtensions` | estreitar e alargar o filtro sem recriar a raiz |

Invariantes que continuam passando sem alteração: `TestStableIds`, `TestCompaction`, `TestWatcher`, `TestLargeChunks`, `TestMetrics`, `TestLifecycle`. `TestGitIgnoreAdvanced` passa a registrar a raiz com `["*"]` para isolar a semântica de gitignore do novo filtro de extensões.

`installer/tests/Installer.Tests.ps1` ganha três asserções, sem executar registro real: ordem de argumentos do `claude mcp add` (§4), presença da página de extensões no wizard e sobrevivência do token `*` à normalização do bootstrap.

## 8. Critérios de aceite

1. `dotnet build -c Release` sem warnings; suíte completa verde.
2. Reconcile sem alterações: nenhum novo segmento, `Version` inalterada.
3. Alterar um arquivo em raiz de 28k arquivos publica versão nova sem reler nem recomprimir a árvore.
4. `git status` ou build em `obj/` dentro de uma raiz não produz versão nova.
5. `FindFast-Setup-win-x64.exe` conclui com `parcial=False` e `claude mcp get findfast` aponta para o executável instalado.
