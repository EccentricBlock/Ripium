using System.Globalization;
using System.Text;

namespace Ripium.Medium;

/// <summary>
/// Medium's text comes from JavaScript, where a string can hold half of an emoji (for example when a title is cut
/// at a fixed length). JSON allows that as a lone <c>\uD83D</c> escape, but .NET cannot turn it into a valid string,
/// so it is replaced with U+FFFD before parsing instead of failing the whole article.
/// </summary>
public static class JsonSanitizer
{
    public static string ReplaceLoneSurrogateEscapes(string json)
    {
        if (!json.Contains("\\u", StringComparison.Ordinal)) return json;

        var sb = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\' || i + 1 >= json.Length)
            {
                sb.Append(json[i]);
                continue;
            }

            // Any other escape ("\\", "\"", "\n") is two characters; skipping both keeps "\\ud800" (a backslash
            // followed by the text "ud800") from being mistaken for a surrogate escape.
            if (json[i + 1] != 'u' || !TryReadHex4(json, i + 2, out var unit))
            {
                sb.Append(json[i]).Append(json[i + 1]);
                i++;
                continue;
            }

            if (unit is >= 0xD800 and <= 0xDBFF
                && i + 11 < json.Length
                && json[i + 6] == '\\' && json[i + 7] == 'u'
                && TryReadHex4(json, i + 8, out var low) && low is >= 0xDC00 and <= 0xDFFF)
            {
                sb.Append(json, i, 12); // a valid pair
                i += 11;
            }
            else if (unit is >= 0xD800 and <= 0xDFFF)
            {
                sb.Append("\\uFFFD");
                i += 5;
            }
            else
            {
                sb.Append(json, i, 6);
                i += 5;
            }
        }
        return sb.ToString();
    }

    private static bool TryReadHex4(string s, int index, out int value)
    {
        value = 0;
        return index + 4 <= s.Length
               && int.TryParse(s.AsSpan(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
