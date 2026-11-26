using System.Text;
using System.Text.RegularExpressions;

namespace WinTtsAs.Core;

public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (ch == '\n' || ch == '\t' || !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().Trim();
        return Regex.Replace(result, @"\n{3,}", "\n\n");
    }
}

public static class TextChunker
{
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[\.\!\?…])\s+|\n{2,}",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return [text];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var sentence in SentenceBoundary.Split(text))
        {
            if (string.IsNullOrWhiteSpace(sentence))
            {
                continue;
            }

            foreach (var piece in SplitLong(sentence.Trim(), maxChars))
            {
                if (current.Length == 0)
                {
                    current.Append(piece);
                }
                else if (current.Length + 1 + piece.Length <= maxChars)
                {
                    current.Append(' ').Append(piece);
                }
                else
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                    current.Append(piece);
                }
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks.Count == 0 ? [text] : chunks;
    }

    private static IEnumerable<string> SplitLong(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                for (var i = 0; i < word.Length; i += maxChars)
                {
                    yield return word.Substring(i, Math.Min(maxChars, word.Length - i));
                }

                continue;
            }

            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxChars)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                yield return current.ToString();
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
