using System.Text.RegularExpressions;

namespace Ripium.Output;

/// <summary>Downloads Medium images next to the Markdown file and rewrites the document to link to them.</summary>
/// <remarks>The caller owns <paramref name="http"/> and disposes it.</remarks>
/// <param name="overwrite">Replace an existing file of the same name. Without it a new name is chosen, so an earlier
/// export (or anything else in the folder) is never overwritten.</param>
/// <param name="maxBytes">Largest image accepted; a bigger one is treated as a failed download.</param>
/// <param name="perImageTimeout">Time allowed for one whole image, headers and body. <c>HttpClient.Timeout</c> covers only the
/// headers when the body is streamed, so a server sending one byte per second would otherwise hold a download forever.</param>
public sealed partial class ImageDownloader(HttpClient http, int maxParallel = 4, bool overwrite = false, long maxBytes = ImageDownloader.DefaultMaxBytes, TimeSpan? perImageTimeout = null)
{
    public const long DefaultMaxBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan DefaultImageTimeout = TimeSpan.FromSeconds(60);

    public static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Downloads every URL into <paramref name="directory"/> (created on first success).
    /// Returns a map of remote URL to file name. An image that fails for any reason is reported through
    /// <paramref name="warn"/> and left out of the map, so its link stays remote: one bad image never aborts the export.
    /// Only cancellation by the caller stops the batch.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DownloadAsync(
        IReadOnlyList<string> urls, string directory, Action<string> warn, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        using var gate = new SemaphoreSlim(maxParallel);
        var tasks = urls.Select(async (url, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(perImageTimeout ?? DefaultImageTimeout);
                var name = await DownloadOneAsync(url, index + 1, directory, timeout.Token);
                lock (result) result[url] = name;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                warn($"Could not download {url}: {Describe(ex)}. Keeping the remote link.");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private static string Describe(Exception ex) => ex is OperationCanceledException ? "the download timed out" : ex.Message;

    private async Task<string> DownloadOneAsync(string url, int number, string directory, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "image/*");
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // An HTML error page with status 200 must not be saved as "photo.jpeg".
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mediaType != "application/octet-stream")
            throw new IOException($"the server sent {mediaType}, not an image");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new IOException($"the image is larger than {maxBytes / (1024 * 1024)} MB");

        var id = url[(url.LastIndexOf('/') + 1)..];
        var name = FileName(number, id, mediaType);
        Directory.CreateDirectory(directory);
        if (!overwrite) name = UnusedName(directory, name);

        // Write beside the final name and rename when complete, so a failed or interrupted download never leaves a
        // truncated image that looks like a good one.
        var final = Path.Combine(directory, name);
        var partial = final + ".part";
        try
        {
            await using (var file = File.Create(partial))
                await CopyWithLimitAsync(await response.Content.ReadAsStreamAsync(ct), file, ct);
            File.Move(partial, final, overwrite);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return name;
    }

    private async Task CopyWithLimitAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new IOException($"the image is larger than {maxBytes / (1024 * 1024)} MB");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>"01-a.jpg" if free, else "01-a-2.jpg", "01-a-3.jpg", ...</summary>
    private static string UnusedName(string directory, string name)
    {
        if (!File.Exists(Path.Combine(directory, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}{ext}";
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    /// <summary>"1*abc.jpeg" becomes "01-1-abc.jpeg"; an id without an extension gets one from the content type.</summary>
    public static string FileName(int number, string imageId, string? contentType)
    {
        var safe = UnsafeChars().Replace(imageId, "-").Trim('-');
        if (safe.Length == 0) safe = "image";
        var ext = Path.GetExtension(safe);
        if (ext.Length is < 2 or > 5)
        {
            ext = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/avif" => ".avif",
                _ => ".img",
            };
            safe += ext;
        }
        return $"{number:00}-{safe}";
    }

    /// <summary>
    /// Points the document at the downloaded copies: image links <c>](remoteUrl)</c> and the frontmatter's
    /// <c>preview_image: "remoteUrl"</c>. Bare URLs in prose are left alone.
    /// </summary>
    public static string RewriteLinks(string markdown, IReadOnlyDictionary<string, string> downloaded, string relativeDirectory)
    {
        var dir = string.Join('/', relativeDirectory.Split('/', '\\').Select(Uri.EscapeDataString));
        foreach (var (url, name) in downloaded)
        {
            var local = $"{dir}/{Uri.EscapeDataString(name)}";
            markdown = markdown
                .Replace($"]({url})", $"]({local})", StringComparison.Ordinal)
                .Replace($"preview_image: \"{url}\"", $"preview_image: \"{local}\"", StringComparison.Ordinal);
        }
        return markdown;
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeChars();
}
