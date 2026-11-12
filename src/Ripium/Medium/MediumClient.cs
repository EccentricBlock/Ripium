using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ripium.Cli;

namespace Ripium.Medium;

public class MediumException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Medium answered with a non-200 HTTP status.</summary>
public sealed class MediumHttpException(HttpStatusCode statusCode, string message) : MediumException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>The response is bigger than we are willing to hold in memory. Retrying would not make it smaller.</summary>
public sealed class ResponseTooLargeException(long limit)
    : MediumException($"Medium's response is larger than {limit / (1024 * 1024)} MB");

/// <summary>Medium answered successfully but there is no such post (deleted, private or a wrong id). Never retried.</summary>
public sealed class PostUnavailableException(string postId)
    : MediumException($"Post is unavailable or does not exist: {postId}");

/// <summary>Fetches a post from Medium's GraphQL endpoint using the same request shape as Freedium.</summary>
public sealed class MediumClient : IDisposable
{
    private const string Endpoint = "https://medium.com/_/graphql";

    // Deliberately odd (unterminated) User-Agent copied from Freedium: do not "fix".
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 15_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1 (compatible; YandexMobileBot/3.0;";

    private readonly HttpClient _http;
    private readonly string? _cookies;
    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;
    private readonly long _maxResponseBytes;

    public const long DefaultMaxResponseBytes = 64L * 1024 * 1024;

    /// <param name="attempts">Total tries, including the first.</param>
    /// <param name="retryDelay">Wait before the 2nd try; it doubles for each further try.</param>
    public MediumClient(string? cookies = null, int attempts = 2, HttpMessageHandler? handler = null,
        TimeSpan? timeout = null, TimeSpan? retryDelay = null, long maxResponseBytes = DefaultMaxResponseBytes)
    {
        _maxResponseBytes = maxResponseBytes;
        _cookies = string.IsNullOrWhiteSpace(cookies) ? null : cookies;
        _attempts = Math.Max(1, attempts);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Returns the resolved <c>data.post</c> object, or throws <see cref="MediumException"/>.</summary>
    public async Task<JsonObject> GetPostAsync(string postId, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_retryDelay * (1 << (attempt - 1)), ct);
            try
            {
                return await QueryOnceAsync(postId, ct);
            }
            catch (Exception ex) when (ex is MediumException or HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (!IsRetryable(ex)) throw;
                last = ex;
            }
        }
        var reason = last is TaskCanceledException ? "the request timed out" : last?.Message;
        throw new MediumException($"Failed to fetch post {postId}: {reason}", last);
    }

    /// <summary>
    /// Transient failures (network errors, timeouts, 408, 429, 5xx, an unparseable body) are worth another try.
    /// A missing post and other 4xx statuses are answers, and repeating the request only adds delay.
    /// </summary>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        PostUnavailableException or ResponseTooLargeException => false,
        MediumHttpException h => h.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500,
        _ => true,
    };

    private async Task<JsonObject> QueryOnceAsync(string postId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["operationName"] = "FullPostQuery",
            ["variables"] = new JsonObject
            {
                ["postId"] = postId,
                ["postMeteringOptions"] = new JsonObject(),
            },
            ["query"] = GraphQlQuery.FullPostQuery,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var h = request.Headers;
        h.TryAddWithoutValidation("X-APOLLO-OPERATION-ID", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        h.TryAddWithoutValidation("X-APOLLO-OPERATION-NAME", "FullPostQuery");
        h.TryAddWithoutValidation("Accept", "multipart/mixed; deferSpec=20220824, application/json, application/json");
        h.TryAddWithoutValidation("Accept-Language", "en-US");
        h.TryAddWithoutValidation("X-Obvious-CID", "android");
        h.TryAddWithoutValidation("X-Xsrf-Token", "1");
        h.TryAddWithoutValidation("X-Client-Date", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        h.TryAddWithoutValidation("User-Agent", UserAgent);
        h.TryAddWithoutValidation("Cache-Control", "public, max-age=-1");
        if (_cookies is not null)
            h.TryAddWithoutValidation("Cookie", _cookies);

        using var response = await _http.SendAsync(request, ct);
        // Decode the bytes ourselves: a Content-Type with an unknown charset makes ReadAsStringAsync throw, and the
        // endpoint serves UTF-8 JSON.
        var text = await ReadBodyAsync(response, ct);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var hint = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? " (Medium or Cloudflare may be blocking this client)"
                : "";
            throw new MediumHttpException(response.StatusCode, $"Medium returned HTTP {(int)response.StatusCode}{hint}: {Truncate(text, 200)}");
        }

        return ExtractPost(text, postId);
    }

    /// <summary>Reads at most the configured number of bytes: a hostile or broken server must not decide how much memory we use.</summary>
    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > _maxResponseBytes) throw new ResponseTooLargeException(_maxResponseBytes);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _maxResponseBytes) throw new ResponseTooLargeException(_maxResponseBytes);
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Parses a GraphQL response body and resolves <c>__ref</c> pointers inside <c>data.post</c>.</summary>
    public static JsonObject ExtractPost(string responseText, string postId)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(JsonSanitizer.ReplaceLoneSurrogateEscapes(responseText)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) // ArgumentException: duplicate keys
        {
            throw new MediumException(
                $"Medium returned something other than JSON (blocked, rate-limited or malformed): {Truncate(Messages.OneLine(responseText.TrimStart()), 80)}", ex);
        }
        if (root is null) throw new MediumException("Medium returned a non-object JSON response");

        // JsonNode builds an object's property table lazily, so a duplicate key throws ArgumentException at the first
        // indexer access, not at Parse. Everything that touches the tree therefore sits inside this try.
        try
        {
            return ExtractPost(root, postId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidCastException)
        {
            var what = ex is ArgumentException ? "is malformed (duplicate property names?)" : "has an unexpected shape";
            throw new MediumException($"Medium's response {what}: {ex.Message}", ex);
        }
    }

    private static JsonObject ExtractPost(JsonObject root, string postId)
    {
        if (root["data"] is not JsonObject data)
        {
            // No data at all means the request failed, which is different from "this post does not exist".
            var message = ((root["errors"] as JsonArray)?.FirstOrDefault() as JsonObject)?["message"]?.ToString();
            throw new MediumException($"Medium returned an error: {message ?? "no data in response"}");
        }
        if (data["post"] is not JsonObject post)
            throw new PostUnavailableException(postId);

        return GraphQlRefResolver.Resolve(post, data) as JsonObject
               ?? throw new MediumException("Medium's response has an unexpected shape: the post is not an object");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
