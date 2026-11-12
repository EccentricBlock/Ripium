using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ripium.Medium;

namespace Ripium.Markdown;

public sealed record RenderOptions(int ImageWidth = 1400, bool IncludeFrontmatter = true);

/// <param name="Markdown">The full document (frontmatter, title and body), ending in a newline.</param>
/// <param name="ImageUrls">Distinct remote image URLs the document links to (body and preview image), in order.</param>
public sealed record RenderResult(string Markdown, string Title, IReadOnlyList<string> ImageUrls, IReadOnlyList<string> Warnings);

/// <summary>Converts a Medium <see cref="Post"/> into Markdown. Modelled on Freedium's Markdown renderer.</summary>
public sealed partial class MarkdownRenderer(RenderOptions? options = null)
{
    private const double SimilarityThreshold = 0.80;
    private const int LeadingParagraphWindow = 4;
    private const int MinimumLengthForFuzzyMatch = 15;

    private readonly RenderOptions _options = options ?? new RenderOptions();
    private readonly List<string> _imageUrls = [];
    private readonly List<string> _warnings = [];

    public static RenderResult Render(Post post, RenderOptions? options = null) => new MarkdownRenderer(options).RenderPost(post);

    public RenderResult RenderPost(Post post)
    {
        _imageUrls.Clear();
        _warnings.Clear();

        var title = (post.Title ?? "").Trim();
        var subtitle = post.PreviewContent?.Subtitle?.Trim();
        var body = DropRedundantLeadingParagraphs(post, ref title, ref subtitle);

        var blocks = new List<string>();
        if (title.Length > 0) blocks.Add("# " + EscapeHeadingText(title));
        if (!string.IsNullOrWhiteSpace(subtitle)) blocks.Add(Emphasize(InlineMarkdown.EscapeText(subtitle)));
        if (!_options.IncludeFrontmatter && Byline(post) is { Length: > 0 } byline) blocks.Add(byline);

        RenderBody(body, blocks);

        var sb = new StringBuilder();
        if (_options.IncludeFrontmatter)
        {
            var preview = post.PreviewImage?.Id is { Length: > 0 } pid && TryImageUrl(pid, out var previewUrl) ? previewUrl : null;
            sb.Append(Frontmatter.Build(post, title, subtitle, preview)).Append('\n');
        }
        sb.Append(string.Join("\n\n", blocks)).Append('\n');
        return new RenderResult(sb.ToString(), title, _imageUrls.ToList(), _warnings.Select(InlineMarkdown.ReplaceControlCharacters).ToList());
    }

    // ---- leading-paragraph cleanup -------------------------------------------------------------------------

    /// <summary>
    /// Medium repeats the title (as H2/H3/H4), the subtitle and the tags at the top of the body. Within the first
    /// few paragraphs, drop those repeats since we print them ourselves. A title or subtitle that Medium truncated
    /// with an ellipsis is completed from the body text.
    /// </summary>
    private static List<Paragraph> DropRedundantLeadingParagraphs(Post post, ref string title, ref string? subtitle)
    {
        var tagTitles = post.Tags?.Select(t => t.DisplayTitle?.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToHashSet() ?? [];
        var result = new List<Paragraph>();
        var all = post.Paragraphs;

        for (var pos = 0; pos < all.Count; pos++)
        {
            var p = all[pos];
            var text = (p.Text ?? "").Trim();

            if (pos < LeadingParagraphWindow && text.Length > 0)
            {
                if (p.Type is "H2" or "H3" or "H4" or "H1" && title.Length > 0 && Matches(text, title))
                {
                    if (IsTruncated(title)) title = text;
                    continue;
                }
                if (p.Type == "H4" && tagTitles.Contains(text)) continue;
                if (p.Type is "H4" or "P" && !string.IsNullOrEmpty(subtitle) && Matches(text, subtitle))
                {
                    if (IsTruncated(subtitle))
                    {
                        // The preview subtitle is just the start of this paragraph: keep the paragraph, drop the stub.
                        subtitle = null;
                    }
                    else
                    {
                        continue;
                    }
                }
            }
            result.Add(p);
        }
        return result;
    }

    // Only Medium's own truncation marker counts. Three dots are ordinary punctuation ("Wait for it...") and must
    // not make a legitimate title look cut off.
    private static bool IsTruncated(string s) => s.EndsWith('…');

    private static string StripEllipsis(string s) => s.TrimEnd('…').TrimEnd();

    private static bool Matches(string paragraphText, string reference)
    {
        if (IsTruncated(reference))
            return paragraphText.StartsWith(StripEllipsis(reference), StringComparison.Ordinal);

        var a = LettersAndDigits(paragraphText);
        var b = LettersAndDigits(reference);
        // Same words, whatever the case and punctuation. Two texts that normalise to nothing ("???" and "!!!", two different
        // emoji) say nothing about each other, so only an identical pair counts.
        if (a.Length == 0 && b.Length == 0) return paragraphText.Trim() == reference.Trim();
        if (a == b) return true;

        // Fuzzy matching only for longer texts that agree on every digit. "Chapter 1" and "Chapter 2" are 89% alike but
        // are different headings, and dropping one would lose article content (Freedium's 80% rule does).
        return Math.Min(a.Length, b.Length) >= MinimumLengthForFuzzyMatch
               && Digits(a) == Digits(b)
               && TextSimilarity.Ratio(paragraphText, reference) > SimilarityThreshold;
    }

    // '#', '+' and a few other symbols change the meaning of a title: "C#" and "C++" are different headings.
    private static string LettersAndDigits(string text) =>
        string.Concat(text.Where(c => char.IsLetterOrDigit(c) || c is '#' or '+' or '&' or '%' or '$' or '@' or '=').Select(char.ToLowerInvariant));

    private static string Digits(string text) => string.Concat(text.Where(char.IsDigit));

    private string? Byline(Post post)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(post.Creator?.Name)) parts.Add("By " + InlineMarkdown.EscapeText(post.Creator!.Name));
        if (post.FirstPublishedAt is { } ms && Timestamps.TryFromUnixMilliseconds(ms) is { } date)
            parts.Add(date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
        if (post.ReadingTime is { } rt && rt > 0 && rt < 10_000)
            parts.Add($"{(int)Math.Ceiling(rt)} min read");
        return parts.Count == 0 ? null : Emphasize(string.Join(" · ", parts));
    }

    // ---- block rendering -----------------------------------------------------------------------------------

    private void RenderBody(List<Paragraph> paragraphs, List<string> blocks)
    {
        for (var i = 0; i < paragraphs.Count;)
        {
            var p = paragraphs[i];
            switch (p.Type)
            {
                case "ULI":
                case "OLI":
                {
                    var type = p.Type;
                    var items = new List<Paragraph>();
                    while (i < paragraphs.Count && paragraphs[i].Type == type) items.Add(paragraphs[i++]);
                    AddIfNotEmpty(blocks, RenderList(items, ordered: type == "OLI"));
                    continue;
                }
                case "PRE":
                {
                    var group = new List<Paragraph>();
                    while (i < paragraphs.Count && paragraphs[i].Type == "PRE") group.Add(paragraphs[i++]);
                    AddIfNotEmpty(blocks, RenderCodeBlock(group));
                    continue;
                }
            }

            AddIfNotEmpty(blocks, RenderSingle(p));
            i++;
        }
    }

    private string? RenderSingle(Paragraph p)
    {
        switch (p.Type)
        {
            case "H1": case "H2": return Heading(p, 2);
            case "H3": return Heading(p, 3);
            case "H4": return Heading(p, 4);
            case "P":
            case "SECTION_CAPTION":
                return InlineMarkdown.Render(p.Text, p.Markups);
            case "BQ":
                return Quote(InlineMarkdown.Render(p.Text, p.Markups));
            case "PQ":
                return Quote(Emphasize(InlineMarkdown.Render(p.Text, p.Markups, ignoreEm: true)));
            case "IMG":
                return RenderImage(p);
            case "MIXTAPE_EMBED":
                return RenderMixtape(p);
            case "IFRAME":
                return RenderIframe(p);
            default:
                _warnings.Add($"Unsupported paragraph type '{p.Type}' ({p.Id}); rendered as text");
                return InlineMarkdown.Render(p.Text, p.Markups);
        }
    }

    private static string? Heading(Paragraph p, int level)
    {
        // A heading is one line. Flatten line breaks *before* rendering, one character for one, so offsets stay valid and
        // the engine keeps blanks outside delimiters; turning the rendered hard break into a space afterwards left
        // "** x**" (an opener followed by a blank) when bold started after a line break.
        var oneLine = p.Text?.Replace('\n', ' ').Replace('\r', ' ');
        var text = InlineMarkdown.Render(oneLine, p.Markups);
        return text.Length == 0 ? null : new string('#', level) + " " + InlineMarkdown.EscapeAtxClosingSequence(text);
    }

    private static string EscapeHeadingText(string title) => InlineMarkdown.EscapeAtxClosingSequence(InlineMarkdown.EscapeText(title));

    private static string? Quote(string rendered) =>
        rendered.Length == 0 ? null : string.Join('\n', rendered.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));

    /// <summary>
    /// Wraps the whole text in emphasis, with blanks moved outside the asterisks ("*quote *" is not italic).
    /// Emphasis may span a hard line break, and must: a link or code span can cross a line break, and closing the
    /// emphasis at every break would cut straight through it.
    /// </summary>
    private static string Emphasize(string rendered)
    {
        var (lead, core, trail) = InlineMarkdown.SplitBlanks(rendered);
        return core.Length == 0 ? "" : lead + "*" + core + "*" + trail;
    }

    private static string? RenderList(List<Paragraph> items, bool ordered)
    {
        var lines = new List<string>();
        var n = 0;
        foreach (var item in items)
        {
            var text = InlineMarkdown.Render(item.Text, item.Markups);
            if (text.Length == 0) continue;
            n++;
            var marker = ordered ? $"{n}. " : "- ";
            var indent = new string(' ', marker.Length);
            var parts = text.Split('\n');
            lines.Add(marker + parts[0]);
            lines.AddRange(parts.Skip(1).Select(l => l.Length == 0 ? "" : indent + l));
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Consecutive PRE paragraphs form a single code block. Markups inside code are ignored.</summary>
    private static string? RenderCodeBlock(List<Paragraph> group)
    {
        var code = string.Join('\n', group.Select(p => InlineMarkdown.ReplaceControlCharacters(p.Text).Replace("\r", "").TrimEnd('\n')));
        if (string.IsNullOrWhiteSpace(code)) return null;

        var lang = group.Select(p => p.CodeBlockMetadata?.Lang?.Trim()).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? "";
        lang = lang.Split([' ', '\t', '\n', '\r', '`'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!SafeLanguage().IsMatch(lang)) lang = ""; // the info string shares a line with the fence; anything odd could alter the code

        var longest = 0;
        for (int i = 0, run = 0; i < code.Length; i++)
        {
            run = code[i] == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{lang}\n{code}\n{fence}";
    }

    private string? RenderImage(Paragraph p)
    {
        if (string.IsNullOrEmpty(p.Metadata?.Id)) return null;
        if (!TryImageUrl(p.Metadata.Id, out var url))
        {
            _warnings.Add($"Skipped image with unusable id '{p.Metadata.Id}' ({p.Id})");
            return null;
        }

        var image = $"![{InlineMarkdown.EscapeText(p.Metadata.Alt)}]({url})";
        if (!string.IsNullOrWhiteSpace(p.Href))
        {
            var target = InlineMarkdown.FormatDestination(p.Href);
            if (InlineMarkdown.IsSafeUrl(target)) image = $"[{image}]({target})";
        }

        var caption = Emphasize(InlineMarkdown.Render(p.Text, p.Markups, ignoreEm: true));
        return caption.Length == 0 ? image : image + "\n\n" + caption;
    }

    /// <summary>
    /// A link card. Medium lays the text out as "Title\nDescription…domain", with a bold markup over the title
    /// and an italic one over the description (they share a range when there is no description).
    /// </summary>
    private static string? RenderMixtape(Paragraph p)
    {
        var text = p.Text ?? "";
        var href = p.MixtapeMetadata?.Href
                   ?? p.Markups?.FirstOrDefault(m => m?.Type == "A" && !string.IsNullOrEmpty(m.Href))?.Href
                   ?? p.Href;
        var target = string.IsNullOrWhiteSpace(href) ? null : InlineMarkdown.FormatDestination(href);
        if (target is null || !InlineMarkdown.IsSafeUrl(target))
            return InlineMarkdown.Render(text, p.Markups);

        string title, description = "";
        var strong = p.Markups?.FirstOrDefault(m => m?.Type == "STRONG" && InlineMarkdown.Slice(text, m.Start, m.End).Length > 0);
        if (strong is not null)
        {
            title = InlineMarkdown.Slice(text, strong.Start, strong.End);
            var em = p.Markups!.FirstOrDefault(m => m?.Type == "EM" && m.Start >= strong.End);
            if (em is not null) description = InlineMarkdown.Slice(text, em.Start, em.End);
        }
        else
        {
            var lines = text.Split('\n');
            title = lines[0];
            if (lines.Length > 1) description = lines[1];
        }

        title = InlineMarkdown.EscapeText(title);
        if (title.Length == 0) title = InlineMarkdown.EscapeText(href);
        var link = $"> [**{title}**]({target})";
        var desc = InlineMarkdown.EscapeText(description);
        return desc.Length == 0 ? link : $"{link}\\\n> {desc}";
    }

    /// <summary>Embeds (tweets, gists, videos) can't be inlined as Markdown, so link to them.</summary>
    private static string? RenderIframe(Paragraph p)
    {
        var media = p.Iframe?.MediaResource;
        string? url = null;
        if (!string.IsNullOrWhiteSpace(media?.IframeSrc))
        {
            var candidate = InlineMarkdown.FormatDestination(UnwrapEmbedly(media!.IframeSrc!));
            if (InlineMarkdown.IsSafeUrl(candidate)) url = candidate;
        }
        if (url is null && media?.Id is { } id && SafeMediaId().IsMatch(id))
            url = $"https://medium.com/media/{id}";
        if (url is null) return null;

        var label = string.IsNullOrWhiteSpace(media?.Title) ? "Embedded content" : "Embedded content: " + media!.Title!.Trim();
        return $"[{InlineMarkdown.EscapeText(label)}]({url})";
    }

    // \z, not $: "$" also matches before a trailing newline, which would smuggle a line break into a URL
    [GeneratedRegex(@"^[A-Za-z0-9]{1,64}\z")]
    private static partial Regex SafeMediaId();

    [GeneratedRegex(@"^[A-Za-z0-9_+#.\-]{1,40}\z")]
    private static partial Regex SafeLanguage();

    /// <summary>
    /// Medium wraps YouTube/Vimeo/etc. in an embedly widget URL whose <c>url</c> query parameter is the real page.
    /// Prefer that, since it is the link a reader actually wants.
    /// </summary>
    public static string UnwrapEmbedly(string iframeSrc)
    {
        if (Uri.TryCreate(iframeSrc, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith("embedly.com", StringComparison.OrdinalIgnoreCase))
        {
            var target = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
            if (Uri.TryCreate(target, UriKind.Absolute, out var inner) && inner.Scheme is "http" or "https")
                return inner.ToString();
        }
        return iframeSrc;
    }

    // ---- images --------------------------------------------------------------------------------------------

    // Ids look like "1*g6_rwCXG8HuOBKVI-NM3Qw.jpeg". Anything else could change the meaning of the URL we build.
    // '@' is real: retina variants are "1*abc@2x.png"
    [GeneratedRegex(@"^[A-Za-z0-9*._~@-]{1,200}\z")]
    private static partial Regex SafeImageId();

    public static string ImageUrl(string imageId, int width) => $"https://miro.medium.com/v2/resize:fit:{width}/{imageId}";

    /// <summary>Builds the remote URL for an image id and records it for downloading; false if the id is unusable.</summary>
    private bool TryImageUrl(string imageId, out string url)
    {
        url = "";
        if (!SafeImageId().IsMatch(imageId) || imageId.Contains("..", StringComparison.Ordinal)) return false;
        url = ImageUrl(imageId, _options.ImageWidth);
        if (!_imageUrls.Contains(url)) _imageUrls.Add(url);
        return true;
    }

    private static void AddIfNotEmpty(List<string> blocks, string? block)
    {
        if (!string.IsNullOrWhiteSpace(block)) blocks.Add(block);
    }
}
