using System.Globalization;
using System.Text;

namespace Ripium.Output;

public static class FileNames
{
    // Most file systems cap a name at 255 bytes; leave room for ".md" and the "_assets" folder suffix.
    private const int MaxSlugBytes = 150;

    /// <summary>
    /// Lower-case slug for a title: letters and digits of any script, with other characters collapsed to single
    /// dashes. Latin letters lose their accents ("Café" becomes "cafe"); other scripts are kept as they are, so a
    /// Russian or Chinese title still gives a readable file name. Returns <paramref name="fallback"/> if nothing is left.
    /// </summary>
    public static string Slugify(string? title, string fallback)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        var pendingDash = false;
        foreach (var rune in (title ?? "").EnumerateRunes())
        {
            var folded = Fold(rune);
            if (folded is null)
            {
                pendingDash = true;
                continue;
            }

            var piece = folded.Value.ToString();
            var cost = Encoding.UTF8.GetByteCount(piece) + (pendingDash && sb.Length > 0 ? 1 : 0);
            if (bytes + cost > MaxSlugBytes) break;

            if (pendingDash && sb.Length > 0) sb.Append('-');
            pendingDash = false;
            sb.Append(piece);
            bytes += cost;
        }
        return sb.Length == 0 ? fallback : sb.ToString();
    }

    /// <summary>The text cut to at most <paramref name="maxBytes"/> UTF-8 bytes, never in the middle of a character.</summary>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > maxBytes) break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    /// <summary>The lower-case letter/digit to keep, with Latin accents removed, or null for anything else.</summary>
    private static Rune? Fold(Rune rune)
    {
        if (!Rune.IsLetterOrDigit(rune)) return null;
        var lower = Rune.ToLowerInvariant(rune);

        // "é" decomposes to "e" + a combining mark; keep the base only when it is a plain ASCII letter.
        var decomposed = lower.ToString().Normalize(NormalizationForm.FormD);
        var first = Rune.GetRuneAt(decomposed, 0);
        var onlyMarksFollow = true;
        for (var i = first.Utf16SequenceLength; i < decomposed.Length; i += Rune.GetRuneAt(decomposed, i).Utf16SequenceLength)
            onlyMarksFollow &= CharUnicodeInfo.GetUnicodeCategory(Rune.GetRuneAt(decomposed, i).Value) == UnicodeCategory.NonSpacingMark;

        return onlyMarksFollow && first.IsAscii && Rune.IsLetterOrDigit(first) ? first : lower;
    }
}
