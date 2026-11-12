using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace Ripium.Medium;

public sealed class InvalidMediumUrlException(string message) : Exception(message);

/// <summary>
/// Turns whatever the user pasted (a Medium URL, a custom-domain URL, a tracking/redirect wrapper or a bare
/// post id) into a Medium post id. Port of Freedium's <c>validators.py</c>.
/// </summary>
public static partial class MediumUrl
{
    private const int MaxRedirectDepth = 8;

    // Medium post ids are 8-12 hex characters, normally the suffix after the last '-' of the slug.
    [GeneratedRegex(@"-(\b[a-fA-F0-9]{8,12}\b)\??")]
    private static partial Regex DashPrefixedHash();

    [GeneratedRegex(@"(\b[a-fA-F0-9]{8,12}\b)\??")]
    private static partial Regex AnyHash();

    /// <summary>Looks up the <c>Location</c> header of a <c>link.medium.com/&lt;id&gt;</c> short link.</summary>
    public delegate Task<string?> ShortLinkResolver(string shortLinkId, CancellationToken ct);

    public static IReadOnlyList<string> ExtractHashes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var matches = DashPrefixedHash().Matches(text);
        if (matches.Count == 0)
            matches = AnyHash().Matches(text);
        return matches.Select(m => m.Groups[1].Value).ToList();
    }

    public static async Task<string> ResolvePostIdAsync(
        string input, ShortLinkResolver? shortLinks = null, CancellationToken ct = default)
    {
        var trimmed = input?.Trim() ?? "";
        var id = await TryResolveAsync(trimmed, shortLinks, 0, ct) ?? IdFromWholeInput(trimmed);
        return id ?? throw new InvalidMediumUrlException($"Could not find a Medium post id in: {input}");
    }

    /// <summary>
    /// Last resort, as in Freedium: take the last id-shaped token anywhere in the input (a bare slug such as
    /// "my-title-27832c8f6644", or an unusual URL tail like ".../title-27832c8f6644/amp"). The query string and fragment
    /// are cut off first so hash-like tracking values cannot win.
    /// </summary>
    private static string? IdFromWholeInput(string input)
    {
        var end = input.IndexOfAny(['?', '#']);
        return ExtractHashes(end < 0 ? input : input[..end]) is [.., var id] ? id : null;
    }

    private static async Task<string?> TryResolveAsync(string input, ShortLinkResolver? shortLinks, int depth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input) || depth > MaxRedirectDepth) return null;

        // A bare id such as "515dd5a43948".
        if (!input.Contains('/') && !input.Contains('.') && ExtractHashes(input) is [var bare] && bare.Length == input.Length)
            return bare;

        var candidate = input.Contains("://") ? input : "https://" + input;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);

        string? Single(string key) => query.GetValues(key) is { Length: 1 } v ? v[0] : null;

        string? segment;
        if (path.StartsWith("/p/", StringComparison.Ordinal))
        {
            segment = path[3..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        else if (host.Equals("l.facebook.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/l.php", StringComparison.Ordinal))
        {
            return await Follow(Single("u"));
        }
        else if (host.Equals("webcache.googleusercontent.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/search", StringComparison.Ordinal))
        {
            var q = Single("q");
            return await Follow(q is not null && q.StartsWith("cache:", StringComparison.Ordinal) ? q["cache:".Length..] : q);
        }
        else if (host.Equals("google.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/url", StringComparison.Ordinal))
        {
            return await Follow(Single("url") ?? Single("q"));
        }
        else if (host.Equals("12ft.io", StringComparison.OrdinalIgnoreCase))
        {
            return await Follow(Single("q"));
        }
        else if (path.StartsWith("/m/global-identity-2", StringComparison.Ordinal))
        {
            return await Follow(Single("redirectUrl"));
        }
        else if (host.Equals("link.medium.com", StringComparison.OrdinalIgnoreCase))
        {
            if (shortLinks is null) return null;
            var shortId = path.TrimStart('/');
            return await Follow(await shortLinks(shortId, ct));
        }
        else
        {
            // Last path segment ("my-title-27832c8f6644"), then the part after its last '-'.
            // The query string is never consulted, which avoids picking up hash-like tracking values.
            var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            segment = last?.Split('-').Last();
        }

        if (segment is null) return null;
        segment = WebUtility.UrlDecode(segment);
        return ExtractHashes(segment) is [.., var id] ? id : null;

        Task<string?> Follow(string? next) =>
            next is null ? Task.FromResult<string?>(null) : TryResolveAsync(next, shortLinks, depth + 1, ct);
    }

    /// <summary>Default short-link resolver: asks rsci.app.link where the link points, without following it.</summary>
    public static ShortLinkResolver CreateShortLinkResolver(HttpClient? http = null)
    {
        return async (shortId, ct) =>
        {
            var client = http ?? SharedNoRedirectClient;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://rsci.app.link/{shortId}");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.4103.116 Safari/537.36");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Headers.Location?.ToString();
        };
    }

    private static readonly HttpClient SharedNoRedirectClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
}
