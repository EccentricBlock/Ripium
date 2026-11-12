using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ripium.Medium;

namespace Ripium.Markdown;

/// <summary>
/// Applies Medium's inline markups (bold, italic, code, links) to a paragraph's text and escapes everything
/// else so the result is valid Markdown whose visible text equals the original.
/// <para>
/// Medium stores markup offsets as UTF-16 code units, which is exactly how .NET indexes strings, so offsets are
/// used directly (Freedium needs a conversion layer for this because Python indexes by code point).
/// </para>
/// <para>
/// Markups may overlap arbitrarily, which Markdown cannot express directly. The text is cut at every markup
/// boundary; styles that stay active across a boundary remain open and styles that end are closed. When a style
/// has to close while an inner one continues, the inner one is closed and reopened. Longer-lived styles are opened
/// first so that crossings are rare. Code is always innermost because nothing inside a code span is interpreted.
/// </para>
/// </summary>
public static partial class InlineMarkdown
{
    /// <summary>Markups beyond this many in one paragraph are ignored.</summary>
    public const int MaxMarkupsPerParagraph = 2_000;

    private enum Kind { Link = 0, Strong = 1, Em = 2, Code = 3 }

    private sealed record Style(Kind Kind, string Prefix, string Suffix);

    private sealed record Span(int Start, int End, Style Style);

    private sealed record Candidate(int Start, int End, Kind Kind, string? Destination);

    /// <param name="ignoreEm">Drop italic markups (used where the caller wraps the whole text in italics).</param>
    public static string Render(string? text, IReadOnlyList<Markup>? markups, bool ignoreEm = false)
    {
        if (string.IsNullOrWhiteSpace(text) || HasNothingVisible(text)) return "";
        text = ReplaceControlCharacters(text);

        // Trim the paragraph and shift offsets, so trailing newlines never leave a dangling hard break.
        var lead = 0;
        while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;
        var trail = text.Length;
        while (trail > lead && char.IsWhiteSpace(text[trail - 1])) trail--;
        var body = text[lead..trail];

        return Emit(body, BuildSpans(body, markups, lead, ignoreEm));
    }

    // Zero-width spaces and joiners are not "white space" to .NET, so a paragraph holding only those would otherwise
    // be written as an invisible line.
    private static bool HasNothingVisible(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Replaces control characters other than tab and line breaks with U+FFFD, one for one so offsets stay valid. A NUL is
    /// shown as U+FFFD by every parser anyway, and an ESC or C1 character written to a file would be interpreted by a terminal
    /// that merely displays it.
    /// </summary>
    public static string ReplaceControlCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        StringBuilder? result = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsControl(text[i]) || text[i] is '\n' or '\r' or '\t') continue;
            result ??= new StringBuilder(text);
            result[i] = (char)0xFFFD;
        }
        return result?.ToString() ?? text;
    }

    /// <summary>Substring by Medium offsets, clamped to the text and never splitting a surrogate pair.</summary>
    public static string Slice(string text, int? start, int? end)
    {
        if (start is null || end is null) return "";
        var s = SnapStart(text, Clamp(text, start.Value));
        var e = SnapEnd(text, Clamp(text, end.Value));
        return e > s ? text[s..e] : "";
    }

    // ---- span construction -------------------------------------------------------------------------------

    private static List<Span> BuildSpans(string text, IReadOnlyList<Markup>? markups, int shift, bool ignoreEm)
    {
        var result = new List<Span>();
        if (markups is null || markups.Count == 0) return result;

        var candidates = new List<Candidate>();
        var acceptedLinks = new List<Candidate>();

        // Real paragraphs have a handful of markups (the busiest one in the fixtures has about 250). Emit is quadratic in
        // their number, so a response with tens of thousands in one paragraph could occupy minutes; markups past the limit
        // lose their formatting, never their text.
        foreach (var m in markups.Take(MaxMarkupsPerParagraph))
        {
            if (m?.Start is not { } rawStart || m.End is not { } rawEnd) continue;
            var start = SnapStart(text, Clamp(text, (long)rawStart - shift));
            var end = SnapEnd(text, Clamp(text, (long)rawEnd - shift));
            if (end <= start) continue;

            switch (m.Type)
            {
                case "STRONG" or "EM":
                {
                    if (m.Type == "EM" && ignoreEm) break;
                    (start, end) = NormalizeEmphasisEdges(text, start, end);
                    // Emphasis around nothing but punctuation would read as stray asterisks.
                    if (end <= start || !HasLetterOrDigit(text, start, end)) break;
                    candidates.Add(new Candidate(start, end, m.Type == "STRONG" ? Kind.Strong : Kind.Em, null));
                    break;
                }
                case "CODE":
                {
                    (start, end) = Trim(text, start, end);
                    if (end > start) candidates.Add(new Candidate(start, end, Kind.Code, null));
                    break;
                }
                case "A":
                {
                    var href = m.AnchorType == "USER" && !string.IsNullOrEmpty(m.UserId)
                        ? $"https://medium.com/u/{m.UserId}"
                        : m.Href;
                    if (string.IsNullOrWhiteSpace(href)) break;
                    var destination = FormatDestination(href);
                    if (!IsSafeUrl(destination)) break;
                    (start, end) = Trim(text, start, end);
                    foreach (var clipped in SubtractOtherLinks(start, end, destination, acceptedLinks))
                    {
                        // clipping can leave a piece that ends or starts on a blank (a newline at the end of a link
                        // label is a hard break, which parsers discard)
                        var (s, e) = Trim(text, clipped.Start, clipped.End);
                        if (e <= s) continue;
                        var piece = new Candidate(s, e, Kind.Link, destination);
                        acceptedLinks.Add(piece);
                        candidates.Add(piece);
                    }
                    break;
                }
            }
        }

        var codeStyle = CreateCodeStyle(text, candidates);
        var strongStyle = new Style(Kind.Strong, "**", "**");
        var emStyle = new Style(Kind.Em, "*", "*");
        var linkStyles = new Dictionary<string, Style>();

        // Same range as a link, code wins nothing; same range as code, emphasis wins nothing.
        var linkRanges = candidates.Where(c => c.Kind == Kind.Link).Select(c => (c.Start, c.End)).ToHashSet();
        var codeRanges = candidates.Where(c => c.Kind == Kind.Code).Select(c => (c.Start, c.End)).ToHashSet();
        foreach (var c in candidates)
        {
            if (c.Kind == Kind.Code && linkRanges.Contains((c.Start, c.End))) continue;
            if (c.Kind is Kind.Strong or Kind.Em && codeRanges.Contains((c.Start, c.End))) continue;

            Style style;
            switch (c.Kind)
            {
                case Kind.Strong: style = strongStyle; break;
                case Kind.Em: style = emStyle; break;
                case Kind.Code: style = codeStyle!; break;
                default:
                    if (!linkStyles.TryGetValue(c.Destination!, out style!))
                        linkStyles[c.Destination!] = style = new Style(Kind.Link, "[", $"]({c.Destination})");
                    break;
            }
            result.Add(new Span(c.Start, c.End, style));
        }

        // The emitter sees one continuous run where same-style ranges overlap or touch, so judge those runs.
        result = MergeSameStyle(text, result);

        // Formatting that cannot be written validly is dropped rather than leaking as literal asterisks.
        var inner = result.Where(s => s.Style.Kind is Kind.Code or Kind.Link).ToList();
        result.RemoveAll(s => s.Style.Kind is Kind.Strong or Kind.Em && !DelimitersCanFlank(text, s, inner));
        return result;
    }

    /// <summary>
    /// Same-style ranges that overlap, touch or are separated only by blanks form one run in the output (a blank-only
    /// stretch never closes or opens anything), so they must be judged and emitted as one.
    /// </summary>
    private static List<Span> MergeSameStyle(string text, List<Span> spans)
    {
        var merged = new List<Span>();
        foreach (var group in spans.GroupBy(s => s.Style))
        {
            Span? current = null;
            foreach (var span in group.OrderBy(s => s.Start))
            {
                if (current is not null && (span.Start <= current.End || IsBlankRange(text, current.End, span.Start)))
                    current = current with { End = Math.Max(current.End, span.End) };
                else
                {
                    if (current is not null) merged.Add(current);
                    current = span;
                }
            }
            if (current is not null) merged.Add(current);
        }
        return merged;
    }

    /// <summary>
    /// A code or link delimiter directly inside an emphasis delimiter counts as punctuation, and CommonMark only
    /// lets an opener be followed by punctuation when whitespace or punctuation precedes it (a closer, mirrored).
    /// So <c>word**`code`**</c> and <c>**[x](u)**word</c> cannot be written; both would print the asterisks.
    /// Code is always written innermost, so any code covering an edge touches the delimiter. A link only does when
    /// it is the inner one, meaning it opened later or closes first. (When a link and emphasis open together after
    /// a letter, the emitter writes the link outside instead, so openers need no check for links.)
    /// </summary>
    private static bool DelimitersCanFlank(string text, Span emphasis, List<Span> inner)
    {
        var openerTouchesCode = inner.Any(c => c.Style.Kind == Kind.Code && c.Start <= emphasis.Start && c.End > emphasis.Start);
        var closerTouchesDelimiter = inner.Any(c => c.Style.Kind == Kind.Code
            ? c.Start < emphasis.End && c.End >= emphasis.End
            : c.End == emphasis.End && c.Start > emphasis.Start);

        if (openerTouchesCode && BlocksFlanking(RuneBefore(text, emphasis.Start))) return false;
        if (closerTouchesDelimiter && BlocksFlanking(RuneAt(text, emphasis.End))) return false;
        return true;
    }

    /// <summary>Markdown cannot nest links, so a later link only keeps the parts no earlier link covers.</summary>
    private static IEnumerable<(int Start, int End)> SubtractOtherLinks(
        int start, int end, string destination, List<Candidate> accepted)
    {
        var pieces = new List<(int Start, int End)> { (start, end) };
        foreach (var other in accepted.Where(o => o.Destination != destination))
        {
            var next = new List<(int Start, int End)>();
            foreach (var (s, e) in pieces)
            {
                if (e <= other.Start || s >= other.End) { next.Add((s, e)); continue; }
                if (s < other.Start) next.Add((s, other.Start));
                if (e > other.End) next.Add((other.End, e));
            }
            pieces = next;
        }
        return pieces.Where(p => p.End > p.Start);
    }

    /// <summary>One fence for all code spans: longer than any backtick run inside them, padded if one touches an edge.</summary>
    private static Style? CreateCodeStyle(string text, List<Candidate> candidates)
    {
        // Two code ranges that touch form one run, so a backtick at the end of one and one at the start of the next
        // make a run of two that neither range shows alone.
        var codes = new List<string>();
        (int Start, int End)? current = null;
        foreach (var c in candidates.Where(c => c.Kind == Kind.Code).OrderBy(c => c.Start))
        {
            if (current is { } run && (c.Start <= run.End || IsBlankRange(text, run.End, c.Start)))
                current = (run.Start, Math.Max(run.End, c.End));
            else
            {
                if (current is { } done) codes.Add(text[done.Start..done.End]);
                current = (c.Start, c.End);
            }
        }
        if (current is { } last) codes.Add(text[last.Start..last.End]);
        if (codes.Count == 0) return null;
        var fence = new string('`', codes.Max(t => LongestRun(t, '`')) + 1);
        // A span that is split (an emphasis starts inside it) gets new edges in the middle of its content, so pad
        // whenever any backtick is present; CommonMark strips one space from each side of a padded span.
        var pad = codes.Any(t => t.Contains('`')) ? " " : "";
        return new Style(Kind.Code, fence + pad, pad + fence);
    }

    /// <summary>
    /// CommonMark only lets <c>*</c> open emphasis before punctuation when whitespace or punctuation precedes it,
    /// and only lets it close after punctuation when whitespace or punctuation follows. For <c>foo"bar" baz</c> that
    /// means a bold span over <c>"bar"</c> cannot be written, so the quotes are left outside the delimiters.
    /// </summary>
    private static (int Start, int End) NormalizeEmphasisEdges(string text, int start, int end)
    {
        // Moving an edge past punctuation can leave it beside a blank, which must then be trimmed too: repeat until stable.
        while (true)
        {
            var (s, e) = Trim(text, start, end);
            while (s < e && IsPunctuationOrSymbol(RuneAt(text, s)) && BlocksFlanking(RuneBefore(text, s)))
                s += RuneAt(text, s)!.Value.Utf16SequenceLength;
            while (e > s && IsPunctuationOrSymbol(RuneBefore(text, e)) && BlocksFlanking(RuneAt(text, e)))
                e -= RuneBefore(text, e)!.Value.Utf16SequenceLength;
            if (s == start && e == end) return (s, e);
            (start, end) = (s, e);
        }
    }

    // ---- emission ---------------------------------------------------------------------------------------

    // shortcut: the active styles of every interval are found by scanning all spans, so Emit is O(intervals x spans);
    // a paragraph with 2,000 markups takes about 0.1 s and 16,000 about 5 s. Upgrade to a sweep line if paragraphs
    // with ten thousand markups ever appear.
    private static string Emit(string text, List<Span> spans)
    {
        if (spans.Count == 0) return EscapePlain(text, 0, text.Length);

        var boundaries = spans.SelectMany(s => new[] { s.Start, s.End }).Append(0).Append(text.Length).Distinct().Order().ToArray();
        var intervals = boundaries.Length - 1;

        // Styles covering each interval, outer-to-inner by kind.
        var activeAt = new List<Style>[intervals];
        for (var i = 0; i < intervals; i++)
        {
            int a = boundaries[i], b = boundaries[i + 1];
            activeAt[i] = spans.Where(s => s.Start <= a && s.End >= b).Select(s => s.Style).Distinct().OrderBy(s => s.Kind).ToList();
        }

        int RunEnd(Style style, int from)
        {
            var j = from;
            while (j < intervals && activeAt[j].Contains(style)) j++;
            return boundaries[j];
        }

        var sb = new StringBuilder();
        var open = new List<Style>(); // currently open styles, outermost first
        var carry = ""; // trailing blanks of the previous interval, withheld until we know what follows
        var previousEndsWithEscapedBacktick = false;

        // Works out which delimiters to write at the start of interval i and updates <open> to match.
        (StringBuilder Closes, StringBuilder Opens, List<Style> Reopened) Transition(int i, bool letterBefore)
        {
            var active = activeAt[i];
            var closes = new StringBuilder();
            var opens = new StringBuilder();
            var reopened = new List<Style>();
            var toOpen = new List<Style>();

            // Close everything from the first style that no longer applies; survivors above it are reopened.
            var firstStale = open.FindIndex(s => !active.Contains(s));
            if (firstStale >= 0)
            {
                reopened.AddRange(open.Skip(firstStale + 1).Where(active.Contains));
                toOpen.AddRange(reopened);
                for (var k = open.Count - 1; k >= firstStale; k--) closes.Append(open[k].Suffix);
                open.RemoveRange(firstStale, open.Count - firstStale);
            }
            toOpen.AddRange(active.Where(s => !open.Contains(s) && !toOpen.Contains(s)));

            // Code must stay innermost: emphasis markers inside an open code span would be literal text.
            if (toOpen.Any(s => s.Kind != Kind.Code) && open.Find(s => s.Kind == Kind.Code) is { } code)
            {
                closes.Append(code.Suffix);
                open.Remove(code);
                toOpen.Add(code);
            }

            // Whatever lasts longest goes outermost, so the shorter-lived styles close first without a crossing. The
            // exception: right after a letter, "**[" cannot open emphasis, so a link goes outside ("a[**x**](u)").
            // Closing asterisks directly before an opener are the same delimiter run, so they do not hide the letter.
            var afterLetter = letterBefore && closes.ToString().All(c => c == '*');
            foreach (var s in toOpen.OrderBy(s => s.Kind == Kind.Code)
                         .ThenBy(s => afterLetter && s.Kind == Kind.Link ? 0 : 1)
                         .ThenByDescending(s => RunEnd(s, i))
                         .ThenBy(s => s.Kind))
            {
                opens.Append(s.Prefix);
                open.Add(s);
            }
            return (closes, opens, reopened);
        }

        for (var i = 0; i < intervals; i++)
        {
            int a = boundaries[i], b = boundaries[i + 1];

            // Delimiters can't touch blanks on their inner side ("** x**" is not bold), so blanks move outside.
            var inCode = activeAt[i].Any(s => s.Kind == Kind.Code);
            var raw = inCode ? CodeText(text, a, b) : EscapePlain(text, a, b);
            var (lead, core, trail) = SplitBlanks(raw);

            // A blank-only interval has nothing to format. Opening delimiters around it would create an empty span
            // (an empty code span is two backticks, which then merge with their neighbours), so it just waits as a
            // blank until the next interval with content decides which delimiters go before it.
            if (core.Length == 0)
            {
                carry += raw;
                previousEndsWithEscapedBacktick = false;
                continue;
            }

            var letterBefore = carry.Length == 0 && lead.Length == 0 && BlocksFlanking(LastRune(sb));
            var before = open.ToList();
            var (closes, opens, reopened) = Transition(i, letterBefore);

            // Two crossing emphasis styles that close and reopen with no blank between ("*****") cannot be parsed:
            // the delimiters pair up wrongly and leak. Give up the reopened style's formatting for the rest of its run.
            var starsMeetStars = carry.Length == 0 && lead.Length == 0 && closes.Length > 0 && closes[^1] == '*';
            if (starsMeetStars && reopened.Where(s => s.Prefix[0] == '*').ToList() is { Count: > 0 } doomed)
            {
                foreach (var style in doomed)
                    for (var j = i; j < intervals && activeAt[j].Contains(style); j++)
                        activeAt[j].Remove(style);
                open.Clear();
                open.AddRange(before);
                (closes, opens, _) = Transition(i, letterBefore);
            }

            var prefix = closes.Length > 0 || opens.Length > 0
                ? closes.ToString() + carry + lead + opens
                : carry + lead;

            // An escaped backtick directly followed by a code delimiter makes a double-backtick run that parsers
            // read differently; the entity cannot be confused with a delimiter.
            if (previousEndsWithEscapedBacktick && prefix.Length > 0 && prefix[0] == '`')
            {
                sb.Length -= 2;
                sb.Append("&#96;");
            }
            sb.Append(prefix).Append(core);

            carry = trail;
            previousEndsWithEscapedBacktick = !inCode && trail.Length == 0 && core.EndsWith('`');
        }

        for (var k = open.Count - 1; k >= 0; k--) sb.Append(open[k].Suffix);
        return sb.Append(carry).ToString();
    }

    /// <summary>
    /// Code spans take their text verbatim, so a newline would start a new line that block syntax could claim
    /// ("# x" becomes a heading before the span is ever parsed) and nothing inside a span can be escaped.
    /// CommonMark itself renders a newline inside a code span as a space, so write the space directly.
    /// </summary>
    private static string CodeText(string text, int start, int end) =>
        text.Substring(start, end - start).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    // ---- escaping ---------------------------------------------------------------------------------------

    /// <summary>
    /// Escapes <c>full[start..end]</c> as Markdown body text. Newlines become hard line breaks. Characters that
    /// only matter at the start of a line (<c>#</c>, <c>&gt;</c>, list markers, <c>===</c>) are judged against the
    /// line in <paramref name="full"/>, so they are escaped even after indentation or a hard break.
    /// </summary>
    private static string EscapePlain(string full, int start, int end)
    {
        var sb = new StringBuilder(end - start + 8);
        for (var i = start; i < end; i++)
        {
            var c = full[i];
            switch (c)
            {
                // '$' because Obsidian, GitHub and most editors turn $...$ into math, silently eating the text between
                case '\\' or '`' or '*' or '[' or ']' or '<' or '~' or '$':
                    sb.Append('\\').Append(c);
                    break;
                case '_':
                    // Only an underscore with letters on both sides *within this segment* is inert: at a segment edge
                    // the neighbour in the output may be a delimiter, and "_" beside one can open or close emphasis.
                    var intraword = i > start && i + 1 < end && char.IsLetterOrDigit(full[i - 1]) && char.IsLetterOrDigit(full[i + 1]);
                    if (!intraword) sb.Append('\\');
                    sb.Append('_');
                    break;
                case '&':
                    if (LooksLikeEntity(full, i)) sb.Append('\\');
                    sb.Append('&');
                    break;
                case '!':
                    // "!" directly before a link opener turns the link into an image; the next segment may be a link
                    if (i == end - 1 && end < full.Length) sb.Append('\\');
                    sb.Append('!');
                    break;
                case '\n' or '\r':
                    // CRLF, LF and a lone CR are each one line break; a run of them is one hard break
                    sb.Append("\\\n");
                    while (i + 1 < end && (full[i + 1] == '\n' || full[i + 1] == '\r')) i++;
                    break;
                case '#' or '>':
                    if (AtLineStart(full, i)) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '-' or '+':
                    // list bullets, "---" rules, setext underlines and "+---+" grid-table rows
                    if (AtLineStart(full, i) && (i + 1 >= full.Length || IsBlankOrBreak(full[i + 1]) || full[i + 1] is '-' or '='))
                        sb.Append('\\');
                    sb.Append(c);
                    break;
                case '=':
                    // "===" under a line is a setext heading underline; "==x==" is a highlight in Obsidian and Markdig
                    if ((AtLineStart(full, i) && RestOfLineIs(full, i, '=')) || NextToSame(full, i)) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '%':
                    // "%%...%%" is a hidden comment in Obsidian
                    if (NextToSame(full, i)) sb.Append('\\');
                    sb.Append(c);
                    break;
                case ':' or '|':
                    // Extensions read ": text" as a definition, ":::" as a container fence and a "|---|" line as a
                    // table delimiter row; escaping a leading one is harmless everywhere else
                    if (AtLineStart(full, i)) sb.Append('\\');
                    sb.Append(c);
                    break;
                case '.' or ')':
                    if (EndsListMarker(full, i)) sb.Append('\\');
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether the '.' or ')' at <paramref name="i"/> ends an ordered-list marker at the start of its line. CommonMark
    /// has only numbers ("1."); Markdig and Pandoc also accept a single letter ("b.") and roman numerals ("iv.") and
    /// would drop the marker from the visible text. Decided at the punctuation so a markup boundary inside the
    /// marker cannot hide it.
    /// </summary>
    private static bool EndsListMarker(string full, int i)
    {
        if (i + 1 < full.Length && !IsBlankOrBreak(full[i + 1])) return false;

        var k = i;
        while (k > 0 && char.IsAsciiLetterOrDigit(full[k - 1])) k--;
        var run = full.AsSpan(k, i - k);
        if (run.IsEmpty || !AtLineStart(full, k)) return false;

        if (char.IsAsciiDigit(run[0])) return run.Length <= 9 && !run.ContainsAnyExceptInRange('0', '9');
        if (run.Length == 1) return char.IsAsciiLetter(run[0]);
        return run.Length <= 4 && !run.ContainsAnyExcept("ivxlcdmIVXLCDM");
    }

    /// <summary>
    /// A blank is a space the parser treats as whitespace, other than a line break: tab, form feed and the Unicode
    /// "space separator" category (no-break, em, ideographic space ...), which is CommonMark's definition. NEL, U+2028,
    /// U+2029 and vertical tab are *not* whitespace to a parser, so they are not blanks here: treating them as blanks
    /// moved delimiters across them and leaked asterisks.
    /// </summary>
    // Markdig also accepts Unicode spaces after a list marker ("+" then an ideographic space starts a list), so every
    // "followed by a blank" test uses this and not just space and tab.
    private static bool IsBlankOrBreak(char c) => c is '\n' or '\r' || IsBlank(c);

    public static bool IsBlank(char c) => c is '\t' or '\f' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Splits off the blanks at both ends: <c>"  x y "</c> gives <c>("  ", "x y", " ")</c>.</summary>
    public static (string Lead, string Core, string Trail) SplitBlanks(string text)
    {
        var start = 0;
        while (start < text.Length && IsBlank(text[start])) start++;
        var end = text.Length;
        while (end > start && IsBlank(text[end - 1])) end--;
        return (text[..start], text[start..end], text[end..]);
    }

    private static bool IsBlankRange(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!IsBlank(text[i])) return false;
        return true;
    }

    private static bool NextToSame(string full, int i) =>
        (i + 1 < full.Length && full[i + 1] == full[i]) || (i > 0 && full[i - 1] == full[i]);

    private static bool AtLineStart(string full, int i)
    {
        var k = i - 1;
        while (k >= 0 && full[k] is ' ' or '\t') k--;
        return k < 0 || full[k] is '\n' or '\r';
    }

    private static bool RestOfLineIs(string full, int i, char c)
    {
        for (; i < full.Length && full[i] != '\n'; i++)
            if (full[i] != c && full[i] is not (' ' or '\t')) return false;
        return true;
    }

    /// <summary>Escapes plain, single-line text that has no Medium markups (titles, alt text, link labels).</summary>
    public static string EscapeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var single = string.Join(' ', ReplaceControlCharacters(text).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return EscapePlain(single, 0, single.Length);
    }

    /// <summary>
    /// A trailing run of '#' preceded by a space is an ATX heading's closing sequence and would be swallowed:
    /// "Why I love C #" would render as "Why I love C".
    /// </summary>
    public static string EscapeAtxClosingSequence(string headingText) =>
        AtxClosingSequence().Replace(headingText, "$1\\$2");

    [GeneratedRegex(@"(\s)(#+)\s*$")]
    private static partial Regex AtxClosingSequence();

    private static bool LooksLikeEntity(string s, int i)
    {
        // &name; or &#123; or &#x1F;
        var j = i + 1;
        if (j < s.Length && s[j] == '#') j++;
        var n = 0;
        while (j < s.Length && n < 32 && char.IsAsciiLetterOrDigit(s[j])) { j++; n++; } // HTML5 names reach 31 characters
        return n > 0 && j < s.Length && s[j] == ';';
    }

    // ---- URLs ---------------------------------------------------------------------------------------------

    /// <summary>Makes a URL safe inside Markdown's <c>(...)</c> destination.</summary>
    public static string FormatDestination(string href)
    {
        // Medium sometimes returns URLs with markdown-style backslash escapes and a stray trailing backslash.
        href = href.Trim().Replace("\\(", "(").Replace("\\)", ")").Replace("\\[", "[").Replace("\\]", "]").TrimEnd('\\');
        var sb = new StringBuilder(href.Length);
        for (var i = 0; i < href.Length; i++)
        {
            var c = href[i];
            switch (c)
            {
                case ' ': sb.Append("%20"); break;
                case '(': sb.Append("%28"); break;
                case ')': sb.Append("%29"); break;
                case '<': sb.Append("%3C"); break;
                case '>': sb.Append("%3E"); break;
                // The Markdown parser decodes backslash escapes and entities inside a destination *after* we have
                // checked the scheme, so "javascript\:x", "javascript&colon;x" and "java&Tab;script:x" would pass the
                // check and still become javascript: URLs. Making both literal closes that. Real URLs are unaffected:
                // "&utm_medium=post" has no semicolon and is not an entity.
                case '\\': sb.Append("%5C"); break;
                case '&' when LooksLikeEntity(href, i): sb.Append("%26"); break;
                default:
                    // Browsers ignore control characters inside a scheme ("java\tscript:"), so they must not survive.
                    if (!char.IsControl(c)) sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Allows http, https, mailto and tel links plus scheme-less (relative) ones; refuses everything else, notably
    /// <c>javascript:</c>, <c>data:</c> and <c>file:</c>, because Markdown viewers may act on them.
    /// Expects a value already passed through <see cref="FormatDestination"/>.
    /// </summary>
    public static bool IsSafeUrl(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        var m = UrlScheme().Match(destination.TrimStart());
        return !m.Success || m.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto" or "tel";
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):")]
    private static partial Regex UrlScheme();

    // ---- helpers ----------------------------------------------------------------------------------------

    private static int Clamp(string text, long offset) => (int)Math.Clamp(offset, 0, text.Length);

    /// <summary>Never begin in the middle of a surrogate pair.</summary>
    private static int SnapStart(string text, int start) =>
        start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]) ? start - 1 : start;

    /// <summary>Never end in the middle of a surrogate pair.</summary>
    private static int SnapEnd(string text, int end) =>
        end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end + 1 : end;

    private static (int Start, int End) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }

    private static bool HasLetterOrDigit(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (char.IsLetterOrDigit(text, i)) return true;
        return false;
    }

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            if (run > best) best = run;
        }
        return best;
    }

    private static Rune? RuneAt(string s, int i) =>
        i >= 0 && i < s.Length && Rune.TryGetRuneAt(s, i, out var r) ? r : null;

    private static Rune? RuneBefore(string s, int i)
    {
        if (i <= 0 || i > s.Length) return null;
        var j = i - 1;
        if (j > 0 && char.IsLowSurrogate(s[j]) && char.IsHighSurrogate(s[j - 1])) j--;
        return Rune.TryGetRuneAt(s, j, out var r) ? r : null;
    }

    // CommonMark 0.31 counts Unicode symbols (emoji, currency, math) as punctuation; 0.30 and parsers built on it
    // (markdown-it, cmark-gfm 0.29) count only ASCII punctuation/symbols and Unicode "P" categories. The rules below are
    // chosen to be valid under both: a character that *either* edition calls punctuation makes a delimiter fragile, and
    // only whitespace or punctuation under the *narrower* 0.30 definition can rescue it.
    private static bool IsPunctuationOrSymbol(Rune? r) => r is { } x && (Rune.IsPunctuation(x) || Rune.IsSymbol(x));

    private static bool IsPunctuationCommonMark030(Rune r) =>
        r.IsAscii ? Rune.IsPunctuation(r) || Rune.IsSymbol(r) : Rune.IsPunctuation(r);

    /// <summary>
    /// True for a neighbour that is neither whitespace nor punctuation: letters and digits, but also combining marks,
    /// zero-width joiners and (under CommonMark 0.30) emoji and other symbols. Next to such a character a delimiter
    /// that touches punctuation on its other side cannot open or close emphasis.
    /// </summary>
    private static bool BlocksFlanking(Rune? r) => r is { } x && !IsCommonMarkWhitespace(x) && !IsPunctuationCommonMark030(x);

    private static bool IsCommonMarkWhitespace(Rune r) =>
        r.Value is '\t' or '\n' or '\f' or '\r' || Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator;

    private static Rune? LastRune(StringBuilder sb)
    {
        if (sb.Length == 0) return null;
        var last = sb[^1];
        if (char.IsLowSurrogate(last) && sb.Length > 1 && char.IsHighSurrogate(sb[^2])) return new Rune(sb[^2], last);
        return Rune.TryCreate(last, out var rune) ? rune : null;
    }
}
