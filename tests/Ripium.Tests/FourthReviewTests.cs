using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Ripium.Cli;
using Ripium.Markdown;
using Ripium.Medium;
using Ripium.Output;
using YamlDotNet.Serialization;

namespace Ripium.Tests;

/// <summary>Regressions for defects found by the fourth independent review.</summary>
public sealed class FourthReviewTests : IDisposable
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();
    private readonly string _dir = Directory.CreateTempSubdirectory("ripium-fourth-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Transport(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---- 1. duplicate keys: JsonNode reports them lazily, at the first indexer access ---------------------------------

    [Theory]
    [InlineData("""{"data":{"post":{},"post":{}}}""")]
    [InlineData("""{"data":{"post":{}},"data":{}}""")]
    [InlineData("""{"errors":[{"message":"a","message":"b"}]}""")]
    [InlineData("""{"errors":[1],"errors":[2]}""")]
    [InlineData("""{"data":{"post":{"a":1,"a":2}}}""")]
    [InlineData("""{"data":{"post":{"items":[{"x":1,"x":2}]}}}""")]
    public void DuplicateKeysAnywhere_AreMediumExceptions(string body)
    {
        var ex = Assert.ThrowsAny<MediumException>(() => MediumClient.ExtractPost(body, "p"));
        Assert.IsNotType<PostUnavailableException>(ex);
    }

    [Theory]
    [InlineData("""{"data":{"post":{},"post":{}}}""")]
    [InlineData("""{"errors":[{"message":"a","message":"b"}]}""")]
    public async Task DuplicateKeys_ThroughTheRealClientPath_AreMediumExceptions(string body)
    {
        var transport = new Transport(() => Json(body));
        using var client = new MediumClient(null, 1, transport);
        await Assert.ThrowsAnyAsync<MediumException>(() => client.GetPostAsync("abc123abc123"));
    }

    // ---- 2. memory: references are bounded by bytes, responses by size -----------------------------------------------------

    [Fact]
    public void ManyReferencesToABigString_AreBoundedInMemory()
    {
        // 400 references to a 1 MB string allocated 1.5 GB; 2,000 made the real app die with OutOfMemoryException (exit 70)
        var refs = string.Join(",", Enumerable.Repeat("{\"__ref\":\"S:1\"}", 2_000));
        var json = "{\"data\":{\"post\":{\"title\":\"t\",\"items\":[" + refs + "]},\"S:1\":{\"text\":\"" + new string('x', 1_000_000) + "\"}}}";

        var before = GC.GetAllocatedBytesForCurrentThread();
        var post = Post.FromJson(MediumClient.ExtractPost(json, "p"));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("t", post.Title);
        Assert.True(allocated < 700L * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB");
    }

    [Fact]
    public void TheByteBudget_StillResolvesNormalReferences()
    {
        var resolved = MediumClient.ExtractPost("""{"data":{"post":{"a":{"__ref":"E:1"},"b":{"__ref":"E:1"}},"E:1":{"v":"x"}}}""", "p");
        Assert.Equal("x", (string?)resolved["a"]!["v"]);
        Assert.Equal("x", (string?)resolved["b"]!["v"]);
    }

    [Theory]
    [InlineData(true)] // the size is declared in Content-Length
    [InlineData(false)] // the size is only visible while reading
    public async Task AnOversizedResponse_IsRefused_AndNotRetried(bool declared)
    {
        var transport = new Transport(() =>
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"data\":" + new string(' ', 5_000) + "null}")));
            if (!declared) content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new MediumClient(null, 3, transport, retryDelay: TimeSpan.FromMilliseconds(1), maxResponseBytes: 1_000);

        await Assert.ThrowsAsync<ResponseTooLargeException>(() => client.GetPostAsync("abc123abc123"));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task AResponseWithinTheLimit_IsAccepted()
    {
        var transport = new Transport(() => Json("""{"data":{"post":{"title":"ok"}}}"""));
        using var client = new MediumClient(null, 1, transport, maxResponseBytes: 1_000);
        Assert.Equal("ok", (string?)(await client.GetPostAsync("abc123abc123"))["title"]);
    }

    [Fact]
    public void AParagraphWithAbsurdlyManyMarkups_RendersQuickly_AndKeepsItsText()
    {
        // Emit is quadratic in the markups of one paragraph; the cap bounds what a hostile response can cost
        var text = string.Concat(Enumerable.Repeat("word ", 20_000));
        var rng = new Random(5);
        var markups = Enumerable.Range(0, 30_000).Select(_ =>
        {
            var start = rng.Next(text.Length);
            return new Markup { Type = rng.Next(2) == 0 ? "STRONG" : "EM", Start = start, End = Math.Min(text.Length, start + 20) };
        }).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rendered = InlineMarkdown.Render(text, markups);

        Assert.True(stopwatch.Elapsed.TotalSeconds < 10, $"took {stopwatch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(Regex.Replace(text, @"\s+", " ").Trim(), Regex.Replace(Markdig.Markdown.ToPlainText(rendered, CommonMark), @"\s+", " ").Trim());
    }

    // ---- 3. terminal output: one line, no control characters, bounded -------------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\nWrote /etc/passwd\rerror: fake", "a Wrote /etc/passwd error: fake")]
    [InlineData("esc\U0000001B[31mred", "esc\U0000FFFD[31mred")]
    [InlineData("tab\there", "tab here")]
    [InlineData(null, "")]
    public void OneLine(string? text, string expected) => Assert.Equal(expected, Messages.OneLine(text));

    [Fact]
    public void OneLine_IsBounded_AndNeverSplitsASurrogatePair()
    {
        var bounded = Messages.OneLine(new string('a', 5_000));
        Assert.True(bounded.Length <= 503);
        Assert.EndsWith("...", bounded);

        var emoji = Messages.OneLine(new string('a', 499) + "\U0001F600" + new string('b', 10));
        Assert.Equal(emoji, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(emoji))); // round-trips: no lone surrogate
    }

    // ---- 4. frontmatter values carry no control characters, like the body --------------------------------------------------

    [Fact]
    public void FrontmatterValues_HaveControlCharactersReplaced_EvenAfterYamlParsing()
    {
        const string hostile = "T\U0000001B[31m\U00000085\U0000009B\U00000007 end";
        var post = new Post
        {
            Title = hostile, PreviewContent = new PreviewContent { Subtitle = hostile },
            Creator = new Creator { Name = hostile }, Collection = new Collection { Name = hostile },
            Tags = [new Tag { DisplayTitle = hostile }],
        };

        var markdown = MarkdownRenderer.Render(post).Markdown;
        var yaml = markdown[4..markdown.IndexOf("\n---\n", 3, StringComparison.Ordinal)];
        var values = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);

        foreach (var key in new[] { "title", "author", "publication", "subtitle" })
            Assert.DoesNotContain(((string)values[key]), char.IsControl);
        Assert.All(((List<object>)values["tags"]).Cast<string>(), tag => Assert.DoesNotContain(tag, char.IsControl));
    }

    // ---- 5. title matching ----------------------------------------------------------------------------------------------------

    private static string Render(string title, string heading)
    {
        var post = new Post { Title = title, Content = new PostContent { BodyModel = new BodyModel { Paragraphs = [new() { Type = "H2", Text = heading }, new() { Type = "P", Text = "body" }] } } };
        return MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown;
    }

    [Theory]
    [InlineData("???", "???")]
    [InlineData("C++", "c++")]
    [InlineData("C#", "C#!")]
    public void IdenticalOrNearIdenticalTitles_AreStillDropped(string title, string heading) => Assert.DoesNotContain("## ", Render(title, heading));

    [Theory]
    [InlineData("C#", "C++")]
    [InlineData("A+", "A-")]
    [InlineData("Rust & Go", "Rust Go")]
    public void HeadingsThatDifferInMeaningfulSymbols_AreKept(string title, string heading) => Assert.Contains("## ", Render(title, heading));

    // ---- 6. cookies ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("uid=1; sid=2", true)]
    [InlineData("a", true)]
    [InlineData("uid=1\r\nX-Evil: 1", false)]
    [InlineData("uid=\U000000E9", false)] // non-ASCII is not valid in a header value either
    [InlineData("uid=1\t", false)]
    [InlineData("uid=1\U0000007F", false)]
    public void IsValidCookieHeader(string cookies, bool expected) => Assert.Equal(expected, CommandLine.IsValidCookieHeader(cookies));

    // ---- 7. a folder name that fits ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a")]
    [InlineData("\U000000E9")] // 2 bytes
    [InlineData("\U00004E2D")] // 3 bytes
    [InlineData("\U00020000")] // 4 bytes
    public void AssetsFolderName_AlwaysFitsTheFileSystem(string unit)
    {
        var stem = string.Concat(Enumerable.Repeat(unit, 250 / Encoding.UTF8.GetByteCount(unit)));
        var name = OutputPaths.AssetsFolderName(Path.Combine(_dir, stem + ".md"));

        Assert.True(Encoding.UTF8.GetByteCount(name) <= 255, $"{Encoding.UTF8.GetByteCount(name)} bytes");
        Assert.EndsWith("_assets", name);
        Directory.CreateDirectory(Path.Combine(_dir, name)); // the file system accepts it
    }

    [Fact]
    public void AssetsFolderName_IsUnchangedForNormalNames() =>
        Assert.Equal("my-story_assets", OutputPaths.AssetsFolderName(Path.Combine(_dir, "my-story.md")));

    [Theory]
    [InlineData("hello", 3, "hel")]
    [InlineData("hello", 10, "hello")]
    [InlineData("h\U000000E9llo", 2, "h")] // the 2-byte letter does not fit in the second byte
    [InlineData("\U0001F600\U0001F600", 5, "\U0001F600")]
    [InlineData("", 5, "")]
    public void TruncateUtf8(string text, int maxBytes, string expected) => Assert.Equal(expected, FileNames.TruncateUtf8(text, maxBytes));
}
