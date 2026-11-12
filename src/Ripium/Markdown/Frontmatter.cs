using System.Globalization;
using System.Text;
using Ripium.Medium;

namespace Ripium.Markdown;

/// <summary>Builds the YAML frontmatter block.</summary>
public static class Frontmatter
{
    public static string Build(Post post, string title, string? subtitle, string? previewImageUrl)
    {
        var sb = new StringBuilder("---\n");
        Add(sb, "title", title);
        if (!string.IsNullOrWhiteSpace(post.Creator?.Name)) Add(sb, "author", post.Creator!.Name!);
        if (post.Creator?.Username is { } username && PlainHttpUrl($"https://medium.com/@{username}") is { } authorUrl) Add(sb, "author_url", authorUrl);
        if (!string.IsNullOrWhiteSpace(post.Collection?.Name)) Add(sb, "publication", post.Collection!.Name!);
        if (!string.IsNullOrWhiteSpace(subtitle)) Add(sb, "subtitle", subtitle!);
        if (Iso(post.FirstPublishedAt) is { } published) sb.Append("published: ").Append(published).Append('\n');
        if (post.UpdatedAt != post.FirstPublishedAt && Iso(post.UpdatedAt) is { } updated) sb.Append("updated: ").Append(updated).Append('\n');
        if (post.ReadingTime is { } rt && rt > 0 && rt < 10_000) sb.Append("reading_time: ").Append(((int)Math.Ceiling(rt)).ToString(CultureInfo.InvariantCulture)).Append('\n');
        // canonicalUrl can be empty and is author-controlled: use the first of the two that is a plain http(s) URL
        if ((PlainHttpUrl(post.CanonicalUrl) ?? PlainHttpUrl(post.MediumUrl)) is { } url) Add(sb, "url", url);
        var tags = post.Tags?.Select(t => t.DisplayTitle).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tags is { Count: > 0 })
        {
            sb.Append("tags:\n");
            foreach (var t in tags) sb.Append("  - ").Append(Quote(InlineMarkdown.ReplaceControlCharacters(t))).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(previewImageUrl)) Add(sb, "preview_image", previewImageUrl!);
        return sb.Append("---\n").ToString();
    }

    /// <summary>
    /// The value if it is an absolute http or https URL without whitespace, control characters or the characters that turn
    /// it into Markdown or HTML ("&lt;javascript:x&gt;" and "[x](javascript:x)" are not URLs). Checked in the form that is
    /// written: consumers may render this value as a link.
    /// </summary>
    private static string? PlainHttpUrl(string? value) =>
        value is { Length: > 0 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '`' or '\\' or '[' or ']')
            ? value
            : null;

    private static void Add(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").Append(Quote(InlineMarkdown.ReplaceControlCharacters(value))).Append('\n');

    private static string? Iso(long? unixMs) =>
        unixMs is { } ms && Timestamps.TryFromUnixMilliseconds(ms) is { } date
            ? date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Always emits a double-quoted YAML scalar, which sidesteps every special-character rule. Characters YAML
    /// forbids or folds (C0/C1 controls, NEL, line/paragraph separators, BOM, non-characters, lone surrogates)
    /// are written as escapes so the document stays parseable.
    /// </summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        sb.Append(c).Append(value[++i]); // a valid pair, e.g. an emoji
                    else if (char.IsSurrogate(c))
                        sb.Append("\\uFFFD");
                    else if (c < 0x20 || c is >= (char)0x7F and <= (char)0x9F)
                        sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    else if (c is (char)0x2028 or (char)0x2029 or (char)0xFEFF or (char)0xFFFE or (char)0xFFFF) // line/paragraph separators, BOM, non-characters
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
