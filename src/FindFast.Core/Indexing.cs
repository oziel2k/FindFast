using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FindFast.Core;

public static class TextIndex
{
    public static IEnumerable<string> Trigrams(string value)
    {
        if (value.Length < 3) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= value.Length - 3; i++)
            if (seen.Add(value.Substring(i, 3))) yield return value.Substring(i, 3);
    }

    public static int[] LineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++) if (content[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    public static (int Line, int Column) OffsetToPosition(int[] starts, int offset)
    {
        var index = Array.BinarySearch(starts, offset);
        if (index < 0) index = ~index - 1;
        return (index + 1, offset - starts[index] + 1);
    }

    public static string Sha256(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static bool IsBinary(byte[] bytes)
    {
        var count = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < count; i++) if (bytes[i] == 0) return true;
        return false;
    }

    public static string GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var regex = Regex.Escape(normalized).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]");
        if (regex.StartsWith(".*/", StringComparison.Ordinal)) regex = "(?:.*/)?" + regex[3..];
        return "^" + regex + "$";
    }
}
