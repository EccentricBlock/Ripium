namespace Ripium.Markdown;

/// <summary>
/// Gestalt pattern matching ratio (Ratcliff/Obershelp), equivalent to Python's
/// <c>difflib.SequenceMatcher(None, a, b).ratio()</c> for short strings. Freedium uses it with a threshold of
/// 0.80 to detect headings and paragraphs that merely repeat the article title or subtitle.
/// </summary>
public static class TextSimilarity
{
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var total = a.Length + b.Length;
        return 2.0 * MatchingChars(a.AsSpan(), b.AsSpan()) / total;
    }

    private static int MatchingChars(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        var (i, j, size) = LongestCommonSubstring(a, b);
        if (size == 0) return 0;
        return size
               + MatchingChars(a[..i], b[..j])
               + MatchingChars(a[(i + size)..], b[(j + size)..]);
    }

    // Earliest (in a, then in b) longest common substring, matching difflib's tie-breaking.
    private static (int I, int J, int Size) LongestCommonSubstring(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int bestI = 0, bestJ = 0, bestSize = 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    current[j] = previous[j - 1] + 1;
                    if (current[j] > bestSize)
                    {
                        bestSize = current[j];
                        bestI = i - bestSize;
                        bestJ = j - bestSize;
                    }
                }
                else
                {
                    current[j] = 0;
                }
            }
            (previous, current) = (current, previous);
            Array.Clear(current);
        }
        return (bestI, bestJ, bestSize);
    }
}
