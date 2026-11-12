using System.Net;
using Ripium.Output;

namespace Ripium.Tests;

public sealed class ImageDownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ripium-images-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string A = "https://miro.medium.com/v2/resize:fit:1400/1*a.jpeg";
    private const string B = "https://miro.medium.com/v2/resize:fit:1400/0*noext";

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private static HttpResponseMessage Image(byte[] bytes, string contentType = "image/jpeg")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/" + contentType.Split('/')[1]);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Delivers some bytes and then fails, like a connection dropped mid-download.</summary>
    private sealed class DroppingStream : Stream
    {
        private int _sent;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_sent++ == 0) { buffer.Span[0] = 0xFF; return 1; }
            throw new IOException("connection dropped");
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private ImageDownloader Downloader(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, int parallel = 4) =>
        new(new HttpClient(new Transport(respond)), parallel);

    [Fact]
    public async Task DownloadsEveryImage_WithNumberedNames_AndNoLeftoverTempFiles()
    {
        var downloader = Downloader((_, _) => Task.FromResult(Image([1, 2, 3])));
        var warnings = new List<string>();

        var map = await downloader.DownloadAsync([A, B], _dir, warnings.Add);

        Assert.Empty(warnings);
        Assert.Equal("01-1-a.jpeg", map[A]);
        Assert.Equal("02-0-noext.jpg", map[B]); // extension taken from the content type
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_dir, map[A])));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task SendsAnImageAcceptHeaderAndABrowserUserAgent()
    {
        HttpRequestMessage? seen = null;
        var downloader = Downloader((r, _) => { seen = r; return Task.FromResult(Image([1])); });
        await downloader.DownloadAsync([A], _dir, _ => { });
        Assert.Equal("image/*", seen!.Headers.GetValues("Accept").Single());
        Assert.Contains("Chrome", string.Join(" ", seen.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task OneFailingImage_DoesNotAbortTheOthers_AndIsReported()
    {
        var downloader = Downloader((r, _) => Task.FromResult(r.RequestUri!.ToString() == A
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Image([9])));
        var warnings = new List<string>();

        var map = await downloader.DownloadAsync([A, B], _dir, warnings.Add);

        Assert.False(map.ContainsKey(A));
        Assert.True(map.ContainsKey(B));
        var warning = Assert.Single(warnings);
        Assert.Contains(A, warning);
        Assert.Contains("remote link", warning);
    }

    [Fact]
    public async Task ADroppedConnection_LeavesNoTruncatedFile()
    {
        var downloader = Downloader((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) }));
        var warnings = new List<string>();

        var map = await downloader.DownloadAsync([A], _dir, warnings.Add);

        Assert.Empty(map);
        Assert.Contains(A, Assert.Single(warnings));
        Assert.Empty(Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories)
            .Where(f => !Directory.Exists(f))); // no .part, no half-written image
    }

    [Fact]
    public async Task ATimeout_IsReportedAsATimeout_NotAsCancellation()
    {
        var downloader = new ImageDownloader(new HttpClient(new Transport(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }))
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        });
        var warnings = new List<string>();

        var map = await downloader.DownloadAsync([A], _dir, warnings.Add);

        Assert.Empty(map);
        Assert.Contains("timed out", Assert.Single(warnings));
    }

    [Fact]
    public async Task AFileSystemProblem_IsReportedPerImage_NotThrown()
    {
        var blocker = Path.Combine(_dir, "assets");
        File.WriteAllText(blocker, "a file where the folder should be");
        var downloader = Downloader((_, _) => Task.FromResult(Image([1])));
        var warnings = new List<string>();

        var map = await downloader.DownloadAsync([A], blocker, warnings.Add);

        Assert.Empty(map);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheBatch()
    {
        using var cts = new CancellationTokenSource();
        var downloader = Downloader(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }, parallel: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync([A, B], _dir, _ => { }, cts.Token));
    }

    [Fact]
    public async Task ParallelismIsBounded()
    {
        int current = 0, peak = 0;
        var downloader = Downloader(async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            lock (this) peak = Math.Max(peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref current);
            return Image([1]);
        }, parallel: 2);

        await downloader.DownloadAsync(Enumerable.Range(1, 8).Select(i => $"https://miro.medium.com/v2/resize:fit:1400/1*{i}.png").ToList(), _dir, _ => { });

        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public async Task NothingToDownload_CreatesNothing()
    {
        var target = Path.Combine(_dir, "none");
        var map = await Downloader((_, _) => throw new InvalidOperationException()).DownloadAsync([], target, _ => { });
        Assert.Empty(map);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(1, "1*g6_rwCXG8HuOBKVI-NM3Qw.jpeg", null, "01-1-g6_rwCXG8HuOBKVI-NM3Qw.jpeg")]
    [InlineData(12, "0*ZcNX1tTo4wnM3kqO", "image/png", "12-0-ZcNX1tTo4wnM3kqO.png")]
    [InlineData(3, "0*abc", "image/webp", "03-0-abc.webp")]
    [InlineData(4, "0*abc", null, "04-0-abc.img")]
    [InlineData(5, "***", "image/gif", "05-image.gif")]
    [InlineData(6, "..", "image/png", "06-...png")]
    public void FileName(int n, string id, string? contentType, string expected) =>
        Assert.Equal(expected, ImageDownloader.FileName(n, id, contentType));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b\\c")]
    [InlineData("x\0y")]
    public void FileName_CanNeverEscapeTheDirectory(string hostileId)
    {
        var name = ImageDownloader.FileName(1, hostileId, "image/png");
        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
    }

    [Fact]
    public void RewriteLinks_OnlyTouchesImageLinksAndThePreviewImage()
    {
        var md = "---\npreview_image: \"" + A + "\"\n---\n\n![a](" + A + ")\n\nsee " + A + "\n\n![b](" + B + ")";
        var map = new Dictionary<string, string> { [A] = "01-1-a.jpeg" };

        var result = ImageDownloader.RewriteLinks(md, map, "my post_assets");

        Assert.Contains("preview_image: \"my%20post_assets/01-1-a.jpeg\"", result);
        Assert.Contains("![a](my%20post_assets/01-1-a.jpeg)", result);
        Assert.Contains("see " + A, result); // a bare URL in prose is not a link target
        Assert.Contains("![b](" + B + ")", result); // not downloaded: stays remote
    }
}
