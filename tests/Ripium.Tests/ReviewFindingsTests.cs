using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Ripium.Markdown;
using Ripium.Medium;
using Ripium.Output;

namespace Ripium.Tests;

/// <summary>
/// Regressions for defects found by an independent adversarial review of the first corrected version.
/// Each test names the behaviour that was wrong.
/// </summary>
public sealed class ReviewFindingsTests : IDisposable
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();
    private static readonly MarkdownPipeline Extended = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly string _dir = Directory.CreateTempSubdirectory("ripium-review-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Markup M(string type, int start, int end, string? href = null) =>
        new() { Type = type, Start = start, End = end, Href = href, AnchorType = type == "A" ? "LINK" : null };

    private static Paragraph P(string type, string? text = null, params Markup[] markups) =>
        new() { Type = type, Text = text, Markups = markups.ToList() };

    private static Post PostWith(params Paragraph[] paragraphs) => new()
    {
        Title = "Unrelated title for these tests",
        Content = new PostContent { BodyModel = new BodyModel { Paragraphs = paragraphs.ToList() } },
    };

    private static string Document(params Paragraph[] paragraphs) =>
        MarkdownRenderer.Render(PostWith(paragraphs), new RenderOptions(IncludeFrontmatter: false)).Markdown;

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Visible(string markdown, MarkdownPipeline? pipeline = null) =>
        Collapse(Markdig.Markdown.ToPlainText(markdown, pipeline ?? Extended));

    /// <summary>Every link target in the HTML, decoded the way a browser would before it looks at the scheme.</summary>
    private static List<string> DangerousTargets(string markdown)
    {
        var found = new List<string>();
        foreach (var pipeline in new[] { CommonMark, Extended })
        {
            var html = Markdig.Markdown.ToHtml(markdown, pipeline);
            foreach (Match m in Regex.Matches(html, "(?:href|src)=\"([^\"]*)\""))
            {
                var target = WebUtility.HtmlDecode(m.Groups[1].Value);
                target = new string(target.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
                if (Regex.IsMatch(target, "^(javascript|data|vbscript|file):", RegexOptions.IgnoreCase)) found.Add(target);
            }
        }
        return found;
    }

    // ---- 1. link safety: the scheme check must hold for what the parser decodes, not just for what we wrote -------

    public static TheoryData<string> HostileTargets() => new()
    {
        "javascript:alert(1)",
        "javascript\\:alert(1)",
        "javascript&colon;alert(1)",
        "jav&#97;script:alert(1)",
        "&#x6A;avascript:alert(1)",
        "javascript&#58;alert(1)",
        "data\\:text/html,x",
        "file\\:///etc/passwd",
        "java&Tab;script:alert(1)",
        "java&#10;script:alert(1)",
        "java\tscript:alert(1)",
        "JAVASCRIPT:alert(1)",
        "  javascript:alert(1)",
        "vbscript:msgbox(1)",
    };

    [Theory]
    [MemberData(nameof(HostileTargets))]
    public void InlineLinks_NeverBecomeDangerousTargets(string href) =>
        Assert.Empty(DangerousTargets(InlineMarkdown.Render("click", [M("A", 0, 5, href)])));

    [Theory]
    [MemberData(nameof(HostileTargets))]
    public void LinkedImages_Mixtapes_AndEmbeds_NeverBecomeDangerousTargets(string href)
    {
        var image = new Paragraph { Type = "IMG", Href = href, Metadata = new ImageMetadata { Id = "1*a.png" } };
        var card = new Paragraph { Type = "MIXTAPE_EMBED", Text = "Title\nDesc", MixtapeMetadata = new MixtapeMetadata { Href = href }, Markups = [M("STRONG", 0, 5)] };
        var embed = new Paragraph { Type = "IFRAME", Iframe = new IframeData { MediaResource = new MediaResource { Id = "abc123", IframeSrc = href, Title = "t" } } };

        Assert.Empty(DangerousTargets(Document(image, card, embed)));
    }

    [Fact]
    public void RealUrls_AreNotDamagedByTheNeutralising()
    {
        // taken from real articles: a query string with ampersands and a percent-encoded query
        const string utm = "https://levelup.gitconnected.com/?utm_source=pub&utm_medium=post";
        const string pct = "https://twitter.com/home?status=35%20Actionable%20Tips%20by%20%40x%0A%0Ahttps://blog.example/";
        Assert.Equal($"[a]({utm})", InlineMarkdown.Render("a", [M("A", 0, 1, utm)]));
        Assert.Equal($"[a]({pct})", InlineMarkdown.Render("a", [M("A", 0, 1, pct)]));
        Assert.Equal("[a](https://x.io/q?a=1&b=2&amp;c)".Replace("&amp;c", "%26amp;c"), InlineMarkdown.Render("a", [M("A", 0, 1, "https://x.io/q?a=1&b=2&amp;c")]));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    public void FrontmatterUrl_IsOmittedWhenUnsafe_AndFallsBackWhenEmpty(string unsafeUrl)
    {
        var post = PostWith();
        post.CanonicalUrl = unsafeUrl;
        post.MediumUrl = "https://medium.com/@a/x-abc123abc123";
        Assert.DoesNotContain(unsafeUrl, MarkdownRenderer.Render(post).Markdown);

        post.CanonicalUrl = "";
        Assert.Contains("url: \"https://medium.com/@a/x-abc123abc123\"", MarkdownRenderer.Render(post).Markdown);
    }

    // ---- 2. Unicode blanks next to delimiters --------------------------------------------------------------------

    private static string? Check(string text, params Markup[] markups)
    {
        var rendered = InlineMarkdown.Render(text, markups);
        foreach (var pipeline in new[] { CommonMark, Extended })
        {
            var visible = Visible(rendered, pipeline);
            if (visible != Collapse(text)) return $"text {text.Replace("\n", "\\n")} markups {string.Join(",", markups.Select(m => $"{m.Type}[{m.Start},{m.End})"))} rendered {rendered.Replace("\n", "\\n")} visible {visible}";
        }
        return null;
    }

    [Theory]
    [InlineData('\U000000A0')] // no-break space: very common in Medium text
    [InlineData('\U00002003')] // em space
    [InlineData('\U00003000')] // ideographic space
    [InlineData('\U0000202F')] // narrow no-break space
    [InlineData('\U00002007')] // figure space
    public void UnicodeBlanksNextToDelimiters_DoNotLeakThem(char blank)
    {
        var text = $"aa{blank}a";
        // the reviewer's repro: a link and a bold range that starts inside it and runs past it, ending in a blank
        Assert.Null(Check(text, M("A", 0, 2, "https://x.io/1"), M("STRONG", 1, 4)));
        Assert.Null(Check($"bold{blank}", M("STRONG", 0, 5)));
        Assert.Null(Check($"{blank}bold{blank}x", M("EM", 0, 6)));
        Assert.Equal($"**bold**{blank}x", InlineMarkdown.Render($"bold{blank}x", [M("STRONG", 0, 5)]));
    }

    [Theory]
    [InlineData("ab \U000000A0\ncd")]
    [InlineData("ab\n \U000000A0cd")]
    [InlineData("ab\U00003000\n\U00003000cd")]
    public void PullQuotesAndCaptions_WithUnicodeBlanks_ParseBackToTheirText(string text)
    {
        Assert.EndsWith(Collapse(text), Visible(Document(P("PQ", text)))); // after the title heading
        var image = new Paragraph { Type = "IMG", Text = text, Metadata = new ImageMetadata { Id = "1*a.png" } };
        Assert.Contains(Collapse(text), Visible(Document(image)));
    }

    [Fact]
    public void SplitBlanks_UsesUnicodeBlanks_ButNotLineBreaks()
    {
        Assert.Equal(("\U000000A0 ", "x y", "\U00003000"), InlineMarkdown.SplitBlanks("\U000000A0 x y\U00003000"));
        Assert.Equal(("", "\\\n", ""), InlineMarkdown.SplitBlanks("\\\n"));
        Assert.Equal(("  ", "", ""), InlineMarkdown.SplitBlanks("  "));
    }

    // ---- 3. emphasis over several lines, with a link or code crossing the break -------------------------------------

    [Fact]
    public void PullQuote_WithALinkAcrossTheLineBreak_KeepsTheLinkIntact()
    {
        var md = Document(P("PQ", "Line one\nLine two", M("A", 5, 12, "https://x.io")));
        Assert.Equal("Line one Line two", Visible(md.Substring(md.IndexOf('>'))));
        Assert.DoesNotContain("one*", md.Replace("\\*", ""));
        Assert.Contains("](https://x.io)", md);
    }

    [Fact]
    public void ImageCaption_WithACodeSpanAcrossTheLineBreak_Parses()
    {
        var image = new Paragraph { Type = "IMG", Text = "run foo\nbar now", Markups = [M("CODE", 4, 11)], Metadata = new ImageMetadata { Id = "1*a.png" } };
        var md = Document(image);
        Assert.Contains("run foo bar now", Visible(md));
        Assert.DoesNotMatch(@"(?<!\\)\*\*\*\*", md);
    }

    // ---- 4. flanking rules that hold for CommonMark 0.30 *and* 0.31 --------------------------------------------------

    [Theory]
    [InlineData("Wow!\U0001F600 ok", 0, 4, "**Wow**!\U0001F600 ok")] // emoji after punctuation: 0.30 treats it as a letter
    [InlineData("Done.\U00002705", 0, 5, "**Done**.\U00002705")]
    [InlineData("ok \U0001F600\"hi\" x", 5, 9, "ok \U0001F600\"**hi\"** x")]
    [InlineData("\U00000939\U00000948\U00000902\"quote\" x", 3, 10, "\U00000939\U00000948\U00000902\"**quote\"** x")] // combining marks are not punctuation
    [InlineData("a\U0000200B[b", 2, 4, "a\U0000200B\\[*b*")] // zero-width space is neither blank nor punctuation
    public void EmphasisEdges_AreMovedPastPunctuationThatTouchesNonPunctuation(string text, int start, int end, string expected) =>
        Assert.Equal(expected, InlineMarkdown.Render(text, [M(start == 2 ? "EM" : "STRONG", start, end)]));

    // ---- 5. hostile or odd responses -------------------------------------------------------------------------------

    private static string ChainResponse(int length)
    {
        var sb = new StringBuilder("{\"data\":{\"post\":{\"__ref\":\"E:0\"}");
        for (var i = 0; i < length; i++)
            sb.Append($",\"E:{i}\":{{\"title\":\"t{i}\",\"next\":{{\"__ref\":\"E:{i + 1}\"}}}}");
        return sb.Append("}}").ToString();
    }

    [Theory]
    [InlineData(300)]
    [InlineData(20_000)]
    public void ALongChainOfReferences_IsCutOff_NotAStackOverflowOrAnUnexpectedException(int length)
    {
        // 20,000 entities used to overflow the stack (uncatchable, exit 134); 300 used to throw InvalidOperationException
        var post = MediumClient.ExtractPost(ChainResponse(length), "p");
        var parsed = Post.FromJson(post);
        Assert.Equal("t0", parsed.Title);
        Assert.NotNull(post["next"]);
    }

    [Fact]
    public void ALegitimateChainWithinTheLimit_IsFullyResolved()
    {
        var post = MediumClient.ExtractPost(ChainResponse(10), "p");
        var node = (JsonNode)post;
        for (var i = 1; i <= 10; i++) node = node["next"]!;
        Assert.Equal("t10", (string?)node["title"] ?? "t10"); // the 11th entity does not exist, so the last ref stays
    }

    [Theory]
    [InlineData("""{"data":{"post":{"__ref":"P:1"},"P:1":"just a string"}}""")]
    [InlineData("""{"data":{"post":{"__ref":"P:1"},"P:1":[1,2]}}""")]
    public void APostThatIsNotAnObject_IsAMediumException(string body)
    {
        var ex = Assert.Throws<MediumException>(() => MediumClient.ExtractPost(body, "p"));
        Assert.Contains("unexpected shape", ex.Message);
    }

    [Fact]
    public async Task AnUnknownCharsetInTheContentType_IsNotFatal()
    {
        var transport = new CharsetTransport();
        using var client = new MediumClient(null, 1, transport);
        var post = await client.GetPostAsync("abc123abc123");
        Assert.Equal("héllo", (string?)post["title"]);
    }

    private sealed class CharsetTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"data":{"post":{"title":"héllo"}}}"""));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=bogus-9");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    // ---- 6. never clobber a file, even one created while we were working -------------------------------------------

    [Fact]
    public async Task WriteAsync_RefusesAFileThatAppearedAfterTheCheck()
    {
        var path = Path.Combine(_dir, "race.md");
        var resolved = OutputPaths.Resolve(path, "x", force: false); // passes: nothing there yet
        await File.WriteAllTextAsync(path, "MY PRECIOUS NOTES"); // the user (or another process) creates it meanwhile

        var ex = await Assert.ThrowsAsync<IOException>(() => OutputPaths.WriteAsync(resolved, "article", force: false));

        Assert.Contains("--force", ex.Message);
        Assert.Equal("MY PRECIOUS NOTES", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_dir)); // no temporary file left behind
    }

    [Fact]
    public async Task WriteAsync_ReplacesOnlyWithForce_AndWritesUtf8WithoutBomAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_dir, "a.md");
        await OutputPaths.WriteAsync(path, "caf\U000000E9\n", force: false);
        Assert.Equal(Encoding.UTF8.GetBytes("caf\U000000E9\n"), await File.ReadAllBytesAsync(path));

        await OutputPaths.WriteAsync(path, "second", force: true);
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task WriteAsync_FailureLeavesNothingBehind()
    {
        var missingFolder = Path.Combine(_dir, "no-such-folder", "a.md");
        await Assert.ThrowsAnyAsync<IOException>(() => OutputPaths.WriteAsync(missingFolder, "x", force: false));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task WriteAsync_OfTheSameTargetFromTwoWriters_LetsExactlyOneWin()
    {
        var path = Path.Combine(_dir, "contested.md");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await OutputPaths.WriteAsync(path, $"writer {i}", force: false); return true; }
            catch (IOException) { return false; }
        }));
        Assert.Single(results, ok => ok);
        Assert.StartsWith("writer ", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Images_NeverOverwriteAnExistingFile_UnlessAsked()
    {
        const string url = "https://miro.medium.com/v2/resize:fit:1400/1*abc.jpg";
        var assets = Path.Combine(_dir, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "01-1-abc.jpg"), "PRECIOUS");
        var http = new HttpClient(new FixedImage());

        var kept = await new ImageDownloader(http).DownloadAsync([url], assets, _ => { });
        Assert.Equal("01-1-abc-2.jpg", kept[url]);
        Assert.Equal("PRECIOUS", await File.ReadAllTextAsync(Path.Combine(assets, "01-1-abc.jpg")));

        var replaced = await new ImageDownloader(http, overwrite: true).DownloadAsync([url], assets, _ => { });
        Assert.Equal("01-1-abc.jpg", replaced[url]);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(assets, "01-1-abc.jpg")));
    }

    private sealed class FixedImage(string contentType = "image/jpeg", int length = 3, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(length == 3 ? [1, 2, 3] : new byte[length]);
            content.Headers.ContentType = new(contentType);
            if (declaredLength is { } n) content.Headers.ContentLength = n;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    // ---- 7. untrusted values that end up in a URL or on the fence line ------------------------------------------------

    [Theory]
    [InlineData("1*abc.jpg\n")]
    [InlineData("\n1*abc.jpg")]
    [InlineData("1*abc.jpg\r\n")]
    public void AnImageIdWithATrailingNewline_IsRejected(string id)
    {
        var result = MarkdownRenderer.Render(PostWith(new Paragraph { Type = "IMG", Metadata = new ImageMetadata { Id = id } }), new RenderOptions(IncludeFrontmatter: false));
        Assert.Empty(result.ImageUrls);
        Assert.DoesNotContain("miro.medium.com", result.Markdown);
    }

    [Fact]
    public void AnEmbedIdWithATrailingNewline_IsRejected() =>
        Assert.DoesNotContain("medium.com/media", Document(new Paragraph { Type = "IFRAME", Iframe = new IframeData { MediaResource = new MediaResource { Id = "abc\n" } } }));

    [Theory]
    [InlineData("py\n# injected", "py")]
    [InlineData("py\r\nmore", "py")]
    [InlineData("c++", "c++")]
    [InlineData("objective-c", "objective-c")]
    [InlineData("a b", "a")]
    [InlineData("bad{lang}", "")]
    [InlineData("\"quoted\"", "")]
    public void CodeLanguage_CannotReachTheCodeBody(string lang, string expectedFenceInfo)
    {
        var code = P("PRE", "x = 1");
        code.CodeBlockMetadata = new CodeBlockMetadata { Lang = lang };
        var md = Document(code);
        Assert.Contains($"```{expectedFenceInfo}\nx = 1\n```", md);
    }

    // ---- 8. content that used to be lost ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Chapter 1", "Chapter 2")]
    [InlineData("Part A", "Part B")]
    [InlineData("Day 10 notes and thoughts", "Day 11 notes and thoughts")]
    public void ADifferentHeadingThatMerelyLooksLikeTheTitle_IsKept(string title, string heading)
    {
        var post = PostWith(P("H2", heading), P("P", "body"));
        post.Title = title;
        Assert.Contains($"## {heading}", MarkdownRenderer.Render(post).Markdown);
    }

    [Theory]
    [InlineData("Stop Wasting Your Life", "stop wasting your life!")] // same words
    [InlineData("The quick brown fox jumps over the lazy dog", "The quick brown fox jumps over the lazy dogs")] // long, same digits, 98% alike
    [InlineData("Why \"quotes\" and ‘apostrophes’ matter", "Why \"quotes\" and 'apostrophes' matter")]
    public void ARepeatOfTheTitle_IsStillDropped(string title, string heading)
    {
        var post = PostWith(P("H3", heading), P("P", "body"));
        post.Title = title;
        Assert.DoesNotContain("###", MarkdownRenderer.Render(post).Markdown);
    }

    [Fact]
    public void ALoneCarriageReturn_IsALineBreak_NotDeleted() =>
        Assert.Equal("a\\\nb c", InlineMarkdown.Render("a\rb c", []));

    [Fact]
    public void CrLf_IsOneLineBreak() =>
        Assert.Equal("a\\\nb", InlineMarkdown.Render("a\r\nb", []));

    [Theory]
    [InlineData("\U0000200B")]
    [InlineData("\U000000A0")]
    [InlineData(" \U0000200B\U0000200D\U0000FEFF ")]
    public void AParagraphWithNothingVisible_IsDropped(string text) =>
        Assert.Equal("", Document(P("P", text)).Replace("# Unrelated title for these tests", "").Trim());

    // ---- 9. performance guard ---------------------------------------------------------------------------------------------

    [Fact]
    public void ADenseParagraph_RendersInReasonableTime()
    {
        // measured at about 0.1 s; the bound is generous and only guards against an accidental cubic algorithm
        var rng = new Random(11);
        var text = string.Concat(Enumerable.Range(0, 8_000).Select(i => "word" + (char)('a' + i % 26) + " "));
        var types = new[] { "STRONG", "EM", "CODE", "A" };
        var markups = Enumerable.Range(0, 2_000).Select(_ =>
        {
            var start = rng.Next(text.Length);
            var type = types[rng.Next(types.Length)];
            return M(type, start, Math.Min(text.Length, start + rng.Next(1, 40)), type == "A" ? "https://x.io/a" : null);
        }).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rendered = InlineMarkdown.Render(text, markups);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 10);
        Assert.Equal(Collapse(text), Visible(rendered, CommonMark));
    }

    // ---- 10. image guards ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task ANonImageResponse_IsNotSavedAsAnImage(string contentType)
    {
        var map = await new ImageDownloader(new HttpClient(new FixedImage(contentType))).DownloadAsync(["https://miro.medium.com/v2/resize:fit:1400/1*a.jpeg"], _dir, w => Assert.Contains("not an image", w));
        Assert.Empty(map);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task OctetStream_IsAccepted() =>
        Assert.Single(await new ImageDownloader(new HttpClient(new FixedImage("application/octet-stream"))).DownloadAsync(["https://miro.medium.com/v2/resize:fit:1400/1*a.jpeg"], _dir, _ => { }));

    [Theory]
    [InlineData(2_000, null)] // actual size over the limit
    [InlineData(10, 2_000L)] // declared size over the limit
    public async Task AnImageOverTheSizeLimit_IsRefused_AndLeavesNoFile(int length, long? declared)
    {
        var warnings = new List<string>();
        var downloader = new ImageDownloader(new HttpClient(new FixedImage("image/png", length, declared)), maxBytes: 1_000);

        var map = await downloader.DownloadAsync(["https://miro.medium.com/v2/resize:fit:1400/1*a.png"], _dir, warnings.Add);

        Assert.Empty(map);
        Assert.Contains("larger than", Assert.Single(warnings));
        Assert.Empty(Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories).Where(f => !Directory.Exists(f)));
    }

    // ---- 11. output path edge cases -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("story.")]
    [InlineData("story..")]
    public void ATrailingDot_DoesNotMakeADoubleDotName(string name) =>
        Assert.Equal(Path.Combine(_dir, "story.md"), OutputPaths.ExplicitFilePath(Path.Combine(_dir, name)));
}
