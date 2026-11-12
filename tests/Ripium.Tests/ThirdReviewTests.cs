using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Ripium.Cli;
using Ripium.Markdown;
using Ripium.Medium;
using Ripium.Output;

namespace Ripium.Tests;

/// <summary>Regressions for defects found by the third independent review.</summary>
public sealed class ThirdReviewTests : IDisposable
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();
    private static readonly MarkdownPipeline Extended = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly string _dir = Directory.CreateTempSubdirectory("ripium-third-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Markup M(string type, int start, int end, string? href = null) =>
        new() { Type = type, Start = start, End = end, Href = href, AnchorType = type == "A" ? "LINK" : null };

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string? RoundTrip(string text, params Markup[] markups)
    {
        var rendered = InlineMarkdown.Render(text, markups);
        foreach (var pipeline in new[] { CommonMark, Extended })
        {
            var visible = Collapse(Markdig.Markdown.ToPlainText(rendered, pipeline));
            if (visible != Collapse(InlineMarkdown.ReplaceControlCharacters(text))) return $"rendered {rendered.Replace("\n", "\\n")} visible {visible}";
        }
        return null;
    }

    // ---- 1. the blank set is CommonMark's, not .NET's ------------------------------------------------------------

    [Theory]
    [InlineData('\U00000020', true)]
    [InlineData('\t', true)]
    [InlineData('\f', true)]
    [InlineData('\U000000A0', true)]
    [InlineData('\U00002003', true)]
    [InlineData('\U00003000', true)]
    [InlineData('\U0000202F', true)]
    [InlineData('\U00000085', false)] // next line: .NET white space, not CommonMark whitespace
    [InlineData('\U00002028', false)] // line separator
    [InlineData('\U00002029', false)] // paragraph separator
    [InlineData('\U0000000B', false)] // vertical tab
    [InlineData('\U0000200B', false)] // zero-width space
    [InlineData('\n', false)]
    [InlineData('\r', false)]
    [InlineData('a', false)]
    public void IsBlank_MatchesCommonMarkWhitespace(char c, bool expected) => Assert.Equal(expected, InlineMarkdown.IsBlank(c));

    [Theory]
    [InlineData('\U00002028')]
    [InlineData('\U00002029')]
    [InlineData('\U00000085')]
    [InlineData('\U0000000B')]
    public void SeparatorsThatAreNotWhitespaceToAParser_DoNotLeakDelimiters(char separator)
    {
        // the reviewer's repro: bold around a quoted word that follows such a character
        Assert.Null(RoundTrip($"a{separator}\"b\" c", M("STRONG", 2, 5)));
        Assert.Null(RoundTrip($"a{separator}`b` c", M("CODE", 2, 5), M("STRONG", 2, 5)));
        Assert.Null(RoundTrip($"{separator}bold{separator}x", M("STRONG", 0, 6)));
    }

    // ---- 2. HTML5 entity names are up to 31 characters ---------------------------------------------------------------

    [Theory]
    [InlineData("Use &CounterClockwiseContourIntegral; here")]
    [InlineData("&NotNestedGreaterGreater; and &amp; and &#8755;")]
    public void LongEntityNames_AreEscaped(string text)
    {
        var rendered = InlineMarkdown.Render(text, []);
        Assert.Equal(Collapse(text), Collapse(Markdig.Markdown.ToPlainText(rendered, CommonMark)));
        Assert.DoesNotContain(" &C", rendered);
    }

    [Fact]
    public void LongEntityShapedTextInAUrl_IsNeutralisedToo() =>
        Assert.Contains("%26CounterClockwiseContourIntegral;", InlineMarkdown.FormatDestination("https://x.io/?a=&CounterClockwiseContourIntegral;"));

    // ---- 3. long but valid output names ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(240)] // 243 bytes with ".md": valid, but the old temporary name (+37 bytes) overflowed the 255-byte limit
    public async Task WriteAsync_HandlesLongFileNames(int length)
    {
        var path = Path.Combine(_dir, new string('n', length) + ".md");
        await OutputPaths.WriteAsync(path, "content", force: false);
        Assert.Equal("content", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_dir)); // no temporary file left behind
    }

    [Theory]
    [InlineData("foo.md.", "foo.md")]
    [InlineData("foo.", "foo.md")]
    [InlineData("foo..", "foo.md")]
    [InlineData("foo.markdown", "foo.markdown")]
    [InlineData("foo", "foo.md")]
    public void ExplicitFilePath_NeverProducesADoubledExtension(string given, string expectedName) =>
        Assert.Equal(Path.Combine(_dir, expectedName), OutputPaths.ExplicitFilePath(Path.Combine(_dir, given)));

    // ---- 4. hostile response shapes --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"errors":["boom"]}""")]
    [InlineData("""{"errors":[[1]]}""")]
    [InlineData("""{"errors":[{"message":{"nested":true}}]}""")]
    [InlineData("""{"data":{"post":{"title":"a","title":"b"}}}""")] // duplicate keys
    [InlineData("""{"data":{"post":{"__ref":"A:1"},"A:1":7}}""")]
    public void OddResponses_AreMediumExceptions_NeverRawExceptions(string body)
    {
        var ex = Assert.ThrowsAny<MediumException>(() => MediumClient.ExtractPost(body, "p"));
        Assert.IsNotType<PostUnavailableException>(ex);
    }

    [Fact]
    public void ManyReferencesToABigEntity_AreBoundedInSize_NotJustInCount()
    {
        // 45,000 references to a 20 KB entity used to expand to 900 MB (21 s, 3.6 GB) from a 700 KB response
        var big = string.Join(",", Enumerable.Range(0, 4_000));
        var refs = string.Join(",", Enumerable.Repeat("{\"__ref\":\"Big:1\"}", 45_000));
        var json = "{\"data\":{\"post\":{\"title\":\"t\",\"items\":[" + refs + "]},\"Big:1\":{\"numbers\":[" + big + "]}}}";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var post = MediumClient.ExtractPost(json, "p");
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed.TotalSeconds < 20, $"took {stopwatch.Elapsed.TotalSeconds:F1} s");
        Assert.True(post.ToJsonString().Length < 30_000_000, "the expanded post is unreasonably large");
        Assert.Equal("t", (string?)post["title"]);
    }

    // ---- 5. an image download has a time limit for the whole body ---------------------------------------------------------

    private sealed class StallingStream : Stream
    {
        private int _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent++ == 0) { buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, ct); // a server that sends one byte and then goes quiet
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class StallingTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new StallingStream());
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task AStalledBody_TimesOut_AndLeavesNoFile()
    {
        // HttpClient.Timeout stops at the headers when the body is streamed, so this used to run forever
        var downloader = new ImageDownloader(new HttpClient(new StallingTransport()), perImageTimeout: TimeSpan.FromMilliseconds(300));
        var warnings = new List<string>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var map = await downloader.DownloadAsync(["https://miro.medium.com/v2/resize:fit:1400/1*a.png"], _dir, warnings.Add);

        Assert.True(stopwatch.Elapsed.TotalSeconds < 10);
        Assert.Empty(map);
        Assert.Contains("timed out", Assert.Single(warnings));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task CallerCancellation_IsStillDistinguishedFromATimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var downloader = new ImageDownloader(new HttpClient(new StallingTransport()), perImageTimeout: TimeSpan.FromMinutes(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(["https://miro.medium.com/v2/resize:fit:1400/1*a.png"], _dir, _ => { }, cts.Token));
    }

    // ---- 6. frontmatter urls are validated in the form they are written --------------------------------------------------

    private static string Frontmatter(string? canonical, string? medium, string? username = null)
    {
        var post = new Post { Title = "t", CanonicalUrl = canonical, MediumUrl = medium, Creator = new Creator { Name = "n", Username = username } };
        return MarkdownRenderer.Render(post).Markdown;
    }

    [Theory]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.io/a")]
    [InlineData("https://x.io/a b")]
    [InlineData("https://x.io/a\"b")]
    [InlineData("https://x.io/`a`")]
    [InlineData("//x.io/relative")]
    public void FrontmatterUrl_MustBeAPlainHttpUrl_ElseTheNextCandidateIsUsed(string unsafeUrl)
    {
        var markdown = Frontmatter(unsafeUrl, "https://medium.com/@a/x-abc123abc123");
        Assert.DoesNotContain(unsafeUrl, markdown);
        Assert.Contains("url: \"https://medium.com/@a/x-abc123abc123\"", markdown);

        Assert.DoesNotContain("url:", Frontmatter(unsafeUrl, unsafeUrl).Replace("author_url", ""));
    }

    [Fact]
    public void AGoodCanonicalUrl_IsPreferred_AndAnOddUsernameDropsOnlyTheAuthorUrl()
    {
        Assert.Contains("url: \"https://blog.example.com/post\"", Frontmatter("https://blog.example.com/post", "https://medium.com/x"));
        var markdown = Frontmatter("https://blog.example.com/post", null, username: "bad name\"[x]");
        Assert.DoesNotContain("author_url", markdown);
        Assert.Contains("author: \"n\"", markdown);
    }

    // ---- 7. control characters never reach the file or the terminal -------------------------------------------------------

    private static bool HasRawControl(string text) => text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    [Fact]
    public void ControlCharacters_AreReplacedEverywhere()
    {
        const string hostile = "x\U0000001B[31mred\U00000000\U0000007F\U00000090y";
        var paragraphs = new List<Paragraph>
        {
            new() { Type = "P", Text = hostile },
            new() { Type = "H2", Text = hostile },
            new() { Type = "BQ", Text = hostile },
            new() { Type = "PQ", Text = hostile },
            new() { Type = "ULI", Text = hostile },
            new() { Type = "PRE", Text = hostile },
            new() { Type = "IMG", Text = hostile, Metadata = new ImageMetadata { Id = "1*a.png", Alt = hostile } },
            new() { Type = "MIXTAPE_EMBED", Text = hostile, MixtapeMetadata = new MixtapeMetadata { Href = "https://x.io/a" }, Markups = [M("STRONG", 0, 5)] },
            new() { Type = "IFRAME", Iframe = new IframeData { MediaResource = new MediaResource { Id = "abc", Title = hostile } } },
            new() { Type = "SURPRISE" + hostile, Text = "t", Id = hostile },
            new() { Type = "P", Text = "link", Markups = [M("A", 0, 4, "https://x.io/a\U0000001Bb")] },
        };
        var post = new Post
        {
            Title = hostile, PreviewContent = new PreviewContent { Subtitle = hostile },
            Creator = new Creator { Name = hostile, Username = "u" }, Collection = new Collection { Name = hostile },
            Tags = [new Tag { DisplayTitle = hostile }],
            Content = new PostContent { BodyModel = new BodyModel { Paragraphs = paragraphs } },
        };

        var result = MarkdownRenderer.Render(post);

        Assert.False(HasRawControl(result.Markdown), "raw control character in the document");
        Assert.All(result.Warnings, w => Assert.False(HasRawControl(w)));
        Assert.Contains("\U0000FFFD", result.Markdown);
    }

    [Fact]
    public void ControlCharacters_AreReplacedOneForOne_SoMarkupOffsetsStayValid() =>
        Assert.Equal("\U0000FFFD**ab**c", InlineMarkdown.Render("\U00000000abc", [M("STRONG", 1, 3)])); // offsets still point at "ab"

    [Fact]
    public void ReplaceControlCharacters_KeepsTabAndLineBreaks() =>
        Assert.Equal("a\tb\nc\rd\U0000FFFD", InlineMarkdown.ReplaceControlCharacters("a\tb\nc\rd\U0000001B"));

    // ---- 8. no letters means nothing to compare ------------------------------------------------------------------------------

    [Theory]
    [InlineData("\U0001F525\U0001F525", "\U0001F680")]
    [InlineData("???", "!!!")]
    [InlineData("--", "...")]
    public void TextWithoutLettersOrDigits_IsNeverTreatedAsARepeatOfTheTitle(string title, string heading)
    {
        var post = new Post { Title = title, Content = new PostContent { BodyModel = new BodyModel { Paragraphs = [new() { Type = "H2", Text = heading }, new() { Type = "P", Text = "body" }] } } };
        Assert.Contains($"## {InlineMarkdown.EscapeText(heading)}", MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown);
    }

    // ---- 9. command line -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public void WhitespaceOnlyOutput_IsRejected(string value) =>
        Assert.Contains("empty", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["x", "-o", value])).Message);

    [Theory]
    [InlineData("uid=1\r\nX-Injected: yes")]
    [InlineData("uid=1\nsid=2")]
    [InlineData("uid=1\U00000000")]
    public void CookiesWithControlCharacters_AreRejected(string cookies) =>
        Assert.Contains("control characters", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["x", "--cookies", cookies])).Message);

    [Fact]
    public void NormalCookies_AreAccepted() =>
        Assert.Equal("uid=1; sid=2", CommandLine.Parse(["x", "--cookies", "uid=1; sid=2"]).Cookies);

    // ---- 10. the documented usage is the real usage ------------------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ripium.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/cli.md")]
    public void Documentation_QuotesTheCurrentUsageTextVerbatim(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), file)).Replace("\r\n", "\n");
        Assert.Contains(CommandLine.Usage.Replace("\r\n", "\n"), text);
    }
}
