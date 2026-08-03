# FindFast — Especificação técnica

## 1. Visão do produto

O FindFast é um servidor MCP (*Model Context Protocol*) para busca de código e texto em diretórios e repositórios locais. Seu principal consumidor é uma IA ou um agente, não uma interface gráfica. Agentes formulam consultas, refinam expressões e usam os resultados para navegar por bases de código sem precisar varrer todos os arquivos a cada busca.

O núcleo do produto é um índice persistente e incremental. O conteúdo dos arquivos rastreados deve ser processado antecipadamente, de modo que uma consulta normalmente examine apenas um conjunto pequeno de arquivos candidatos. A leitura direta de todos os arquivos é um mecanismo de confirmação ou contingência, não o caminho normal da busca.

### Objetivos

- Retornar resultados relevantes com baixa latência, inclusive em repositórios grandes.
- Expor pelo MCP ferramentas simples, previsíveis e fáceis de combinar por agentes.
- Suportar texto literal, palavras, caminhos e expressões regulares dinâmicas.
- Manter os índices atualizados após criação, alteração, renomeação ou exclusão de arquivos.
- Persistir índices entre execuções e permitir múltiplos diretórios ou repositórios rastreados.
- Limitar volume, tempo e memória de cada consulta para proteger o servidor.

### Fora do escopo inicial

- Busca semântica por embeddings.
- Indexação de conteúdo remoto sem cópia local.
- Substituição ou edição de arquivos.
- Indexação completa de formatos binários proprietários.

## 2. Princípios de arquitetura

1. **Indexar uma vez, consultar muitas vezes.** O custo de leitura e tokenização ocorre na inclusão ou alteração do arquivo.
2. **Filtrar antes de abrir arquivos.** Metadados e trigramas geram a lista de candidatos; o conteúdo original confirma o resultado e produz o contexto.
3. **Sem falso negativo no filtro.** Estruturas probabilísticas podem eliminar candidatos apenas quando essa eliminação é segura. Falsos positivos são aceitáveis e serão confirmados no arquivo.
4. **Consistência eventual explícita.** Cada resposta informa a versão e o estado do índice usado.
5. **Respostas próprias para agentes.** Resultados são estruturados, pagináveis, limitados e trazem contexto suficiente para uma próxima consulta.
6. **Regex é dinâmica.** Padrões enviados pelo MCP são compilados e armazenados em cache em tempo de execução; `GeneratedRegex` não é aplicável a padrões desconhecidos durante o build.

## 3. Arquitetura de alto nível

```text
Cliente MCP / Agente
        |
        v
Servidor MCP (stdio inicialmente)
        |
        +--> Catálogo de raízes e estado do índice
        +--> Planejador de consultas
        |       +--> filtro por caminho/metadados
        |       +--> índice de trigramas
        |       +--> verificação literal/regex
        |
        +--> Serviço de indexação
                +--> descoberta inicial
                +--> fila incremental
                +--> observador de alterações
                +--> armazenamento persistente
```

O servidor MCP e o indexador podem viver no mesmo processo na primeira versão, mas devem ser módulos independentes. Consultas devem continuar disponíveis durante uma atualização; elas usam o último snapshot consistente publicado.

## 4. Modelo do índice

Cada raiz rastreada recebe um `root_id` estável e representa um diretório comum ou a raiz de um repositório Git. O índice deve armazenar, no mínimo:

### Catálogo de raízes

- `root_id`, nome amigável e caminho absoluto canônico.
- tipo (`directory` ou `git_repository`).
- regras de inclusão e exclusão.
- estado: `building`, `ready`, `updating`, `stale` ou `error`.
- versão do índice, data da última atualização e diagnóstico do último erro.

### Catálogo de arquivos

- `file_id` estável dentro da raiz.
- caminho relativo normalizado e caminho absoluto derivável.
- tamanho, horário de modificação e hash de conteúdo.
- indicador de texto/binário, codificação detectada e linguagem/extensão.
- versão em que o arquivo entrou, mudou ou foi removido.
- offsets das quebras de linha, para converter offsets em linha e coluna sem reler todo o prefixo.

### Índice de conteúdo

O índice primário de conteúdo será um índice invertido de trigramas UTF-8:

```text
trigrama -> posting list comprimida de file_id
```

- Trigramas são extraídos do conteúdo normalizado de cada arquivo textual.
- As *posting lists* devem ser ordenadas e comprimidas (por exemplo, delta + varint ou Roaring Bitmap).
- O índice deve suportar interseção eficiente das listas.
- Para pesquisas sem distinção entre maiúsculas e minúsculas, manter uma representação normalizada compatível ou um índice secundário. A normalização escolhida deve ser documentada para Unicode.
- Consultas literais com menos de três bytes usam um índice auxiliar de unigramas/bigramas ou uma varredura limitada nos arquivos elegíveis.

Bloom filters podem ser usados por segmento como otimização adicional, mas não substituem o índice invertido: eles não fornecem diretamente a lista de arquivos candidatos.

### Índice de caminhos

Caminhos devem ter índice separado, permitindo filtrar rapidamente por:

- prefixo, sufixo ou trecho do caminho;
- extensão e linguagem;
- padrões glob (`**/*.cs`, `src/**`);
- raiz ou conjunto de raízes.

## 5. Persistência e snapshots

O índice deve residir em uma pasta de dados fora das raízes rastreadas. O formato pode usar SQLite para catálogo e estado transacional, combinado com segmentos binários próprios para as *posting lists*. Uma implementação inicial totalmente em SQLite é aceitável desde que os benchmarks atendam às metas.

Requisitos:

- gravação atômica por lote;
- recuperação após encerramento inesperado;
- versão de esquema e migração controlada;
- segmentos imutáveis publicados como snapshot;
- compactação em segundo plano para remover versões antigas e arquivos excluídos;
- bloqueio que impeça dois escritores sobre a mesma base, permitindo leitores concorrentes quando seguro;
- comando de reconstrução integral quando o índice estiver incompatível ou corrompido.

O hash de configuração da raiz deve fazer parte da identidade do índice. Alterações em exclusões, normalização ou versão do tokenizador provocam reindexação parcial ou integral, conforme necessário.

## 6. Pipeline de indexação

### Indexação inicial

1. Canonicalizar e validar a raiz.
2. Carregar regras padrão e específicas da raiz.
3. Enumerar arquivos sem seguir links simbólicos por padrão.
4. Descartar arquivos ignorados, binários, inacessíveis ou acima do limite configurado.
5. Detectar codificação, calcular hash e extrair metadados, quebras de linha e trigramas.
6. Gravar lotes em segmentos temporários.
7. Publicar atomicamente um novo snapshot e marcar a raiz como `ready`.

O pipeline usa `System.Threading.Channels` com capacidade limitada. A enumeração produz trabalhos; workers fazem leitura e análise; um único estágio coordenado grava lotes. O paralelismo deve ser configurável e respeitar pressão de memória e capacidade do disco.

### Atualização incremental

`FileSystemWatcher` reduz a latência, mas não é fonte exclusiva de verdade, pois eventos podem ser perdidos ou agrupados. A atualização combina:

- eventos do sistema de arquivos com *debounce* e coalescência;
- comparação de tamanho e `mtime`, seguida de hash quando necessário;
- varredura periódica de reconciliação;
- reindexação explícita solicitada pelo MCP.

Renomeações preservam o `file_id` quando identificáveis. Exclusões geram *tombstones* até a compactação. Arquivos alterados enquanto estão sendo lidos são reagendados.

### Regras de exclusão

Por padrão, respeitar `.gitignore` em repositórios Git e excluir diretórios de alto custo como `.git`, `node_modules`, `bin` e `obj`. O usuário pode sobrescrever essas regras. A resposta de status deve expor as regras efetivas para evitar resultados silenciosamente ausentes.

## 7. Planejamento e execução de buscas

### Busca literal

1. Aplicar filtros de raiz, caminho, extensão, linguagem e tamanho.
2. Extrair os trigramas da expressão e escolher primeiro as listas menos frequentes.
3. Intersectar as listas para obter candidatos.
4. Confirmar a expressão exata no conteúdo atual do arquivo.
5. Retornar ocorrências ordenadas com linha, coluna e contexto.

### Busca por expressão regular

O planejador tenta extrair literais obrigatórios da regex. Quando consegue, usa seus trigramas para reduzir candidatos e depois executa a regex dinâmica apenas nesses arquivos. Quando não consegue extrair um literal seguro — por exemplo, em `\w+` ou alternativas sem trecho comum — executa uma busca limitada nos arquivos que passaram pelos demais filtros e informa que o índice não pôde reduzir a consulta.

Regex deve usar `System.Text.RegularExpressions.Regex` com `RegexOptions.NonBacktracking` quando o padrão for compatível. Caso contrário, usar o motor convencional com timeout obrigatório. Padrões compilados podem ser mantidos em cache LRU por expressão e opções.

### Segurança e limites

Toda consulta aceita ou aplica:

- `max_results` e `max_results_per_file`;
- timeout total e timeout de regex;
- limite de bytes de contexto;
- paginação por cursor opaco;
- cancelamento propagado pelo MCP;
- limite de candidatos para consultas não seletivas.

Resultados parciais devem ser marcados com `truncated: true` e trazer um motivo (`result_limit`, `timeout`, `candidate_limit` ou `cancelled`).

## 8. Contrato MCP

O transporte inicial será `stdio`, com logs enviados exclusivamente para `stderr` para não corromper mensagens JSON-RPC. Um transporte HTTP pode ser adicionado depois sem alterar os contratos das ferramentas.

### Ferramentas mínimas

#### `roots_list`

Lista raízes cadastradas, estado, versão, quantidade de arquivos e data da última atualização.

#### `root_add`

Cadastra uma raiz e inicia sua indexação.

Entrada principal: `path`, `name?`, `include?`, `exclude?`, `respect_gitignore?`.

#### `root_remove`

Remove uma raiz do catálogo e seu índice. Não remove arquivos do diretório rastreado.

#### `index_update`

Agenda reconciliação incremental ou reconstrução de uma raiz.

Entrada principal: `root_id`, `mode` (`incremental` ou `full`), `wait?`.

#### `index_status`

Retorna progresso, fila pendente, versão disponível e erros de indexação.

#### `search_text`

Executa busca literal indexada.

```json
{
  "query": "CreateBounded",
  "root_ids": ["findfast"],
  "path_glob": "**/*.cs",
  "case_sensitive": true,
  "whole_word": false,
  "context_lines": 2,
  "max_results": 100,
  "cursor": null
}
```

#### `search_regex`

Executa regex dinâmica, usando o índice quando for possível extrair literais obrigatórios.

```json
{
  "pattern": "class\\s+([A-Z]\\w+)",
  "root_ids": ["findfast"],
  "path_glob": "**/*.cs",
  "case_sensitive": true,
  "context_lines": 1,
  "max_results": 100,
  "timeout_ms": 5000,
  "cursor": null
}
```

#### `files_find`

Localiza arquivos por caminho, nome, extensão ou linguagem sem consultar seu conteúdo.

#### `file_read`

Lê um intervalo limitado de um arquivo indexado por `root_id`, caminho relativo e faixa de linhas. Isso permite ao agente ampliar um resultado sem receber o arquivo inteiro.

### Formato de resposta de busca

```json
{
  "index_version": 42,
  "index_state": "ready",
  "query_plan": {
    "strategy": "trigram_then_verify",
    "candidate_files": 12
  },
  "matches": [
    {
      "root_id": "findfast",
      "path": "src/Index/TrigramIndex.cs",
      "line": 38,
      "column": 17,
      "match": "CreateBounded",
      "before": ["..."],
      "text": "var channel = Channel.CreateBounded<Job>(options);",
      "after": ["..."]
    }
  ],
  "truncated": false,
  "next_cursor": null,
  "elapsed_ms": 14
}
```

Os caminhos retornados são sempre relativos à raiz. Caminhos absolutos só devem ser expostos quando explicitamente solicitado e permitido pela configuração.

## 9. Tratamento de arquivos

- UTF-8 é o formato preferencial; UTF-16 com BOM deve ser suportado.
- Arquivos com bytes NUL ou detecção binária positiva são ignorados por padrão.
- Arquivos grandes têm limite configurável e podem ser indexados em blocos sobrepostos para preservar correspondências nas fronteiras.
- A confirmação deve detectar se o arquivo mudou após a versão indexada. Nesse caso, pode confirmar sobre o conteúdo atual, omitir o resultado obsoleto e agendar atualização.
- A primeira versão não deve usar `MemoryMappedFile` indiscriminadamente. Leitura sequencial com buffers reutilizados costuma ser mais simples; mapeamento de memória só será adotado se benchmarks demonstrarem ganho para tamanhos específicos.

## 10. Segurança

- Apenas raízes cadastradas podem ser lidas.
- Canonicalizar caminhos e bloquear escape por `..`, links ou junções fora da raiz.
- Não seguir links simbólicos por padrão.
- Tratar conteúdo dos arquivos como dados não confiáveis; nunca executar resultados encontrados.
- Redigir caminhos e trechos sensíveis nos logs.
- Aplicar permissões mínimas à pasta de índices.
- Validar tamanhos de entrada e impedir regex sem timeout.

## 11. Observabilidade

Logs estruturados devem registrar operações e métricas, sem conteúdo integral dos arquivos:

- duração e quantidade de arquivos da indexação;
- bytes lidos, arquivos ignorados e erros por motivo;
- latência p50, p95 e p99 das consultas;
- número de candidatos antes e depois de cada filtro;
- taxa de consultas que exigem varredura por não possuírem literal indexável;
- tamanho em disco, memória utilizada, tamanho da fila e duração de compactações.

O `query_plan` retornado pelo MCP oferece diagnóstico conciso ao agente, mas detalhes internos volumosos ficam disponíveis apenas em modo de depuração.

## 12. Metas de desempenho e benchmarks

As metas devem ser validadas em hardware de referência documentado e com o cache frio e quente medidos separadamente.

- Consulta literal seletiva em índice pronto: p95 inferior a 100 ms em 1 milhão de arquivos.
- Primeira página com até 100 ocorrências: p95 inferior a 250 ms quando houver poucos milhares de candidatos.
- Atualização de um arquivo pequeno visível no índice em até 2 segundos após o último evento estabilizado.
- Servidor ocioso sem manter o conteúdo completo dos arquivos em memória.
- Cancelamento observado em até 100 ms nos estágios controlados pela aplicação.

O conjunto de benchmark deve incluir monorepositório, muitos arquivos pequenos, arquivos grandes, consultas comuns, literais curtos, regex seletiva, regex sem literal obrigatório e grandes listas de exclusão. Otimizações como SIMD, memória mapeada e cache de conteúdo só entram após medição reproduzível.

## 13. Estratégia de testes

- **Unitários:** normalização de caminho, tokenização, trigramas, interseção, extração segura de literais de regex e conversão de offsets.
- **Integração:** ciclo adicionar/indexar/buscar/alterar/excluir, persistência e reinício do servidor.
- **Contrato MCP:** schemas, erros, cancelamento, paginação e ausência de logs em `stdout`.
- **Consistência:** eventos perdidos, alterações durante leitura, renomeação e recuperação após falha no commit.
- **Segurança:** traversal, symlinks/junções, regex patológica, arquivos gigantes e entradas inválidas.
- **Desempenho:** regressões de latência, throughput, alocação e tamanho do índice.

Testes de propriedade devem garantir que todo resultado produzido por uma varredura de referência também seja encontrado pela busca indexada. Essa é a proteção principal contra falsos negativos.

## 14. Fases de entrega

### Fase 1 — MCP pesquisável

- servidor MCP por `stdio`;
- catálogo de raízes;
- indexação inicial persistente;
- busca literal e por caminho;
- `file_read`, limites, cancelamento e testes de contrato.

### Fase 2 — Índice incremental e regex

- observação e reconciliação de alterações;
- índice de trigramas e confirmação de candidatos;
- regex dinâmica com extração segura de literais e timeout;
- snapshots, paginação e métricas de consulta.

### Fase 3 — Escala e robustez

- segmentos comprimidos e compactação;
- otimizações orientadas por benchmark;
- recuperação e migração de esquema;
- transporte HTTP opcional e operação com múltiplos clientes.

## 15. Critérios de aceite do MVP

O MVP estará concluído quando:

1. Um cliente MCP conseguir cadastrar uma raiz, acompanhar a indexação e consultá-la.
2. Reiniciar o processo não exigir reconstruir um índice compatível e íntegro.
3. `search_text`, `files_find` e `file_read` responderem com schemas estáveis e caminhos relativos.
4. Alterar, criar ou excluir um arquivo for refletido após atualização incremental ou reconciliação.
5. Consultas forem canceláveis, pagináveis e protegidas por limites.
6. A suíte comprovar equivalência com uma varredura de referência, sem falsos negativos nos casos suportados.
7. Os benchmarks e o hardware usado forem reproduzíveis e as metas da seção 12 forem medidas.
