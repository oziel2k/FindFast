# Especificação Técnica: FastSearcher .NET 8

Esta especificação detalha a arquitetura de um utilitário de varredura e busca de texto de altíssima performance para Windows, construído em C# (.NET 8+). A arquitetura combina I/O assíncrono profundo, processamento vetorizado (SIMD) e a nova geração de expressões regulares do .NET.

## 1. Arquitetura do Pipeline de Concorrência

Para saturar a banda do SSD NVMe sem travar a CPU, utilizaremos a biblioteca `System.Threading.Channels` no padrão Produtor-Consumidor.

- **Crawler (Produtor):** Varre diretórios usando `Directory.EnumerateFiles` e coloca os caminhos dos arquivos no Canal.
- **Workers (Consumidores):** Múltiplas `Tasks` lendo do Canal simultaneamente. Cada Worker processa um arquivo usando I/O assíncrono.

```csharp
using System.Threading.Channels;

// Canal limitado (Bounded) previne estouro de memória se o SSD for mais rápido que a CPU
var channelOptions = new BoundedChannelOptions(capacity: 10000)
{
    SingleWriter = true,
    SingleReader = false
};
var fileChannel = Channel.CreateBounded<string>(channelOptions);
```

## 2. Leitura Otimizada: I/O e Memory Management

O gargalo comum em C# é a alocação excessiva de strings (Garbage Collector). Usaremos **Memory-Mapped Files** ou **System.IO.Pipelines** para ler bytes crus (UTF-8) e trabalhar exclusivamente com `ReadOnlySpan<byte>`.

```csharp
using System.IO.MemoryMappedFiles;

public static void ScanFileFast(string filePath)
{
    // Mapeia o arquivo direto na memória virtual do Windows (Zero-copy)
    using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

    unsafe
    {
        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

        // Criamos um Span diretamente do ponteiro da memória, sem alocar arrays
        ReadOnlySpan<byte> content = new ReadOnlySpan<byte>(pointer, (int)accessor.Capacity);

        ProcessContent(content);

        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
    }
}
```

## 3. Aceleração de Hardware (SIMD)

No .NET 8, a classe `SearchValues<T>` foi introduzida para utilizar instruções de processador vetoriais (AVX2/AVX-512) na busca de caracteres (ex: encontrar quebras de linha ou caracteres literais em microssegundos).

```csharp
using System.Buffers;

public class SimdScanner
{
    // Pré-compila a busca vetorial (SIMD) para quebras de linha (CR / LF)
    private static readonly SearchValues<byte> _newLines = SearchValues.Create("\r\n"u8);

    public static void ProcessContent(ReadOnlySpan<byte> content)
    {
        int offset = 0;
        while (offset < content.Length)
        {
            // Pula diretamente para a próxima linha analisando dezenas de bytes por ciclo de CPU
            int nextLineIndex = content.Slice(offset).IndexOfAny(_newLines);
            if (nextLineIndex == -1) break;

            offset += nextLineIndex + 1;
        }
    }
}
```

## 4. Regex Compilado (Source Generators)

O C# moderno não avalia Regex em tempo de execução se usarmos os *Source Generators*. A regex é convertida em código C# fortemente tipado durante o *build* (compilação AOT-friendly).

```csharp
using System.Text.RegularExpressions;

public partial class PatternMatcher
{
    // O atributo GeneratedRegex gera o autômato da regex no momento da compilação
    [GeneratedRegex(@"(class|struct)\s+[A-Z]\w*", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    public static partial Regex ClassDefinitionRegex();

    public bool ContainsMatch(ReadOnlySpan<char> text)
    {
        // O motor do .NET 8 consegue executar regex diretamente em Spans (Zero alocação)
        return ClassDefinitionRegex().IsMatch(text);
    }
}
```

## 5. Estrutura do Índice de Trigramas (Bloom Filter Híbrido)

Em vez de um banco de dados pesado, para o utilitário Windows manter-se portátil e residente em memória, utilizamos um **Bloom Filter** simplificado acoplado a dicionários indexados para lidar com a extração de trigramas.

```csharp
using System.Collections.Concurrent;

public class TrigramIndex
{
    // Chave: O Trigram (empacotado em um inteiro de 32 bits para máxima performance)
    // Valor: Lista de IDs de arquivos que contêm esse trigrama
    private ConcurrentDictionary<int, HashSet<int>> _index = new();

    // Função de empacotamento rápido (Exemplo: "cla" -> 0x636C61)
    public static int PackTrigram(byte a, byte b, byte c)
    {
        return (a << 16) | (b << 8) | c;
    }
}
```

### Resumo do Fluxo de Execução Recomendado

1. O usuário insere a Regex. O sistema extrai os trigramas obrigatórios matematicamente.
2. O índice (em memória ou no disco) é consultado. Retorna-se apenas os IDs dos arquivos candidatos.
3. O Channel distribui esses IDs para os workers assíncronos.
4. Cada worker abre o arquivo mapeado na memória (`MemoryMappedFile`).
5. O worker usa SIMD (`SearchValues`) para pular o lixo rapidamente e aplica a Regex do Source Generator (`[GeneratedRegex]`) apenas nos trechos suspeitos.
