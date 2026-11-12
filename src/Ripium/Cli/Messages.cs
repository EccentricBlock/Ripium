using Ripium.Markdown;

namespace Ripium.Cli;

/// <summary>Text printed to the terminal. Anything that came from a server, an article or the command line goes through here.</summary>
public static class Messages
{
    private const int MaxLength = 500;

    /// <summary>
    /// One line, no control characters, bounded length. A line break would let a hostile paragraph type or server message
    /// start a line that looks like our own output ("Wrote /etc/passwd"); an escape sequence would reach the terminal.
    /// </summary>
    public static string OneLine(string? text)
    {
        var clean = InlineMarkdown.ReplaceControlCharacters(text).Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        if (clean.Length <= MaxLength) return clean;
        var cut = char.IsHighSurrogate(clean[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return clean[..cut] + "...";
    }
}
