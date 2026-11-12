using System.Text.RegularExpressions;
using Markdig;
using Ripium.Markdown;
using Ripium.Medium;

namespace Ripium.Tests;

public class MarkdownRendererRobustnessTests
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();

    private static Paragraph P(string type, string? text = null, params Markup[] markups) =>
        new() { Type = type, Text = text, Markups = markups.ToList() };

    private static Post PostWith(IEnumerable<Paragraph> paragraphs, string title = "A Title", string? subtitle = null) => new()
    {
        Title = title,
        PreviewContent = new PreviewContent { Subtitle = subtitle },
        Content = new PostContent { BodyModel = new BodyModel { Paragraphs = paragraphs.ToList() } },
    };

    private static RenderResult Render(Post post) => MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false));

    private static string Body(params Paragraph[] paragraphs) =>
        string.Join("\n\n", Render(PostWith(paragraphs)).Markdown.TrimEnd('\n').Split("\n\n").Skip(1));

    private static string Text(string markdown) => Regex.Replace(Markdig.Markdown.ToPlainText(markdown, CommonMark), @"\s+", " ").Trim();

    // ---- titles and headings -----------------------------------------------------------------------------

    [Fact]
    public void TitleEndingInThreeDots_IsNotMistakenForATruncatedOne()
    {
        // only Medium's own "…" marks truncation; "Wait for it..." is a real title and must survive a similar early heading
        var result = Render(PostWith([P("H3", "Wait for it, here it comes"), P("P", "body")], title: "Wait for it..."));
        Assert.Equal("Wait for it...", result.Title);
        Assert.Contains("### Wait for it, here it comes", result.Markdown);
    }

    [Fact]
    public void TitleEndingInAnEllipsisCharacter_IsStillCompleted() =>
        Assert.Equal("A long title that was cut off", Render(PostWith([P("H3", "A long title that was cut off")], title: "A long title that was…")).Title);

    [Theory]
    [InlineData("Why I love C #", "# Why I love C \\#")]
    [InlineData("Is it ## ?", "# Is it ## ?")]
    [InlineData("# starts with a hash", "# \\# starts with a hash")]
    public void TitlesCannotLoseCharactersToHeadingSyntax(string title, string expected)
    {
        var markdown = Render(PostWith([], title: title)).Markdown;
        Assert.StartsWith(expected, markdown);
        Assert.Equal(title, Text(markdown)); // the parser shows the whole title, literally
    }

    [Fact]
    public void SectionHeadingEndingInAHash_KeepsIt()
    {
        var md = Body(P("H2", "Intro to C #"));
        Assert.Equal("## Intro to C \\#", md);
        Assert.Equal("Intro to C #", Text(md));
    }

    [Fact]
    public void HeadingWithAHardBreak_StaysOnOneLine() =>
        Assert.Equal("## one two", Body(P("H2", "one\ntwo")));

    // ---- images ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a b")]
    [InlineData("x?y")]
    [InlineData("a#b")]
    [InlineData("../../etc/passwd")]
    [InlineData("1*a..b.png")]
    [InlineData("x)y")]
    [InlineData("x\ny")]
    [InlineData("%2e%2e")]
    [InlineData("")]
    public void UnusableImageIds_AreSkipped_WithAWarning(string id)
    {
        var paragraph = new Paragraph { Type = "IMG", Metadata = new ImageMetadata { Id = id } };
        var result = Render(PostWith([paragraph, P("P", "after")]));
        Assert.Empty(result.ImageUrls);
        Assert.DoesNotContain("miro.medium.com", result.Markdown);
        Assert.Contains("after", result.Markdown);
        if (id.Length > 0) Assert.Contains(result.Warnings, w => w.Contains("unusable id"));
    }

    [Theory]
    [InlineData("1*g6_rwCXG8HuOBKVI-NM3Qw.jpeg")]
    [InlineData("0*ZcNX1tTo4wnM3kqO")]
    [InlineData("1*abc~def.png")]
    [InlineData("1*uh6EKAOW6cYpm7katfywbA@2x.png")] // a real retina id; an earlier, stricter rule skipped these images
    public void RealImageIds_AreAccepted(string id)
    {
        var result = Render(PostWith([new Paragraph { Type = "IMG", Metadata = new ImageMetadata { Id = id } }]));
        Assert.Single(result.ImageUrls);
    }

    [Fact]
    public void AnImageThatCannotBeUsed_DoesNotBreakThePreviewImageInTheFrontmatter()
    {
        var post = PostWith([]);
        post.PreviewImage = new ImageRef { Id = "bad id" };
        var markdown = MarkdownRenderer.Render(post).Markdown;
        Assert.DoesNotContain("preview_image", markdown);
    }

    // ---- hostile links in every position -----------------------------------------------------------------------

    [Fact]
    public void ALinkedImageWithAnUnsafeHref_KeepsTheImageButNotTheLink()
    {
        var image = new Paragraph { Type = "IMG", Href = "javascript:alert(1)", Metadata = new ImageMetadata { Id = "1*a.png" } };
        Assert.Equal("![](https://miro.medium.com/v2/resize:fit:1400/1*a.png)", Body(image));
    }

    [Fact]
    public void AMixtapeWithAnUnsafeHref_BecomesPlainText()
    {
        var card = new Paragraph
        {
            Type = "MIXTAPE_EMBED", Text = "Title\nDesc",
            MixtapeMetadata = new MixtapeMetadata { Href = "javascript:alert(1)" },
            Markups = [new Markup { Type = "STRONG", Start = 0, End = 5 }],
        };
        var md = Body(card);
        Assert.DoesNotContain("javascript", md);
        Assert.Contains("Title", md);
    }

    [Fact]
    public void AnIframeWithAnUnsafeSource_FallsBackToTheMediaPage()
    {
        var iframe = new Paragraph { Type = "IFRAME", Iframe = new IframeData { MediaResource = new MediaResource { Id = "abc123", IframeSrc = "javascript:alert(1)", Title = "T" } } };
        Assert.Equal("[Embedded content: T](https://medium.com/media/abc123)", Body(iframe));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("a)b")]
    [InlineData("")]
    public void AnIframeWithAnUnsafeIdAndNoSource_IsSkipped(string id)
    {
        var iframe = new Paragraph { Type = "IFRAME", Iframe = new IframeData { MediaResource = new MediaResource { Id = id } } };
        Assert.Equal("", Body(iframe));
    }

    [Fact]
    public void AMixtapeWhoseMarkupsPointInsideASurrogatePair_StillProducesValidText()
    {
        var text = "\U0001F600 Title\nDescription";
        var card = new Paragraph
        {
            Type = "MIXTAPE_EMBED", Text = text,
            MixtapeMetadata = new MixtapeMetadata { Href = "https://x.io/a" },
            Markups = [new Markup { Type = "STRONG", Start = 1, End = 8 }, new Markup { Type = "EM", Start = 9, End = 20 }],
        };
        var md = Body(card);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) > 0);
        Assert.DoesNotContain('�', md);
        Assert.Equal(md, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(md))); // round-trips: no lone surrogate
    }

    // ---- emphasised lines (pull quotes, captions, subtitles) -----------------------------------------------------

    [Fact]
    public void PullQuote_PutsBlanksOutsideTheEmphasis() =>
        // blanks at the ends stay outside the asterisks ("* quote*" is not emphasis); the one inside the text is fine
        Assert.Equal("> *quote \\\n> second line*", Body(P("PQ", "  quote \nsecond line  ")));

    [Fact]
    public void PullQuote_TellsAHardBreakFromEscapedBackslashes()
    {
        // "end\" followed by a line break renders as three backslashes: two for the literal one, one for the break
        Assert.Equal("> *end\\\\\\\n> next*", Body(P("PQ", "end\\\nnext")));
        // a text that merely ends in a literal backslash is not a hard break
        Assert.Equal("> *end\\\\*", Body(P("PQ", "end\\")));
    }

    [Theory]
    [InlineData("quote \nsecond line")]
    [InlineData("end\\\nnext")]
    [InlineData("  padded  \n  lines  ")]
    [InlineData("**stars** and _underscores_\nand `ticks`")]
    public void PullQuotes_ParseBackToTheirText(string text)
    {
        var md = Body(P("PQ", text));
        Assert.Equal(Regex.Replace(text, @"\s+", " ").Trim(), Text(md.Replace("> ", "")));
    }

    [Fact]
    public void Subtitle_WithMarkdownSyntax_IsShownLiterally()
    {
        var md = Render(PostWith([], subtitle: "- 5 *tips* [for] #devs: <b>ok</b> $5")).Markdown;
        Assert.Equal("- 5 *tips* [for] #devs: <b>ok</b> $5", Text(md.Split("\n\n")[1]));
    }

    // ---- whole documents, parsed ---------------------------------------------------------------------------------

    [Fact]
    public void ParagraphsThatLookLikeBlockSyntax_NeverBecomeBlocks()
    {
        var lookalikes = new[]
        {
            "1. not a list", "- not a bullet", "+ nor this", "* nor this", "# not a heading", "## nor this", "> not a quote",
            "=== not an underline", "--- not a rule", "```not a fence", "~~~ nor this", "| not | a table |", ": not a definition",
            "[ref]: not a link definition", "<div>not html</div>", "    not indented code", "a. not lettered", "iv) not roman",
        };
        var markdown = Render(PostWith(lookalikes.Select(t => P("P", t)))).Markdown;
        var html = Markdig.Markdown.ToHtml(markdown, CommonMark);

        // the only block-level elements are the title heading and one paragraph per lookalike
        Assert.Equal(1, Regex.Matches(html, "<h[1-6]").Count);
        Assert.DoesNotMatch("<(ul|ol|li|blockquote|pre|table|hr)", html);
        Assert.Equal(lookalikes.Length, Regex.Matches(html, "<p>").Count);
    }

    [Fact]
    public void ListItems_WithBlockLookalikes_StayOneItemEach()
    {
        var items = new[] { "- dash", "1. number", "# hash", "> quote", "plain", "multi\nline\n- inner" };
        var markdown = Render(PostWith(items.Select(t => P("ULI", t)))).Markdown;
        var html = Markdig.Markdown.ToHtml(markdown, CommonMark);

        Assert.Equal(items.Length, Regex.Matches(html, "<li>").Count);
        Assert.Equal(1, Regex.Matches(html, "<ul>").Count);
        Assert.DoesNotMatch("<(ol|blockquote|h[2-6])", html);
    }

    [Fact]
    public void OrderedLists_AreOneListWithConsecutiveNumbers()
    {
        var markdown = Render(PostWith(new[] { "a", "b", "c" }.Select(t => P("OLI", t)))).Markdown;
        var html = Markdig.Markdown.ToHtml(markdown, CommonMark);
        Assert.Equal(1, Regex.Matches(html, "<ol>").Count);
        Assert.Equal(3, Regex.Matches(html, "<li>").Count);
    }

    [Fact]
    public void CodeBlocks_SurviveParsingByteForByte()
    {
        var code = "def f():\n\n\n    return \"```\"  \n\t# comment\n";
        var markdown = Render(PostWith([P("PRE", code), P("P", "after")])).Markdown;
        var html = Markdig.Markdown.ToHtml(markdown, CommonMark);
        var inner = Regex.Match(html, "<code[^>]*>(.*?)</code>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(code.TrimEnd('\n') + "\n", System.Net.WebUtility.HtmlDecode(inner));
    }

    [Fact]
    public void BlockquotesAndLists_ContainTheirText()
    {
        var markdown = Render(PostWith([P("BQ", "first line\nsecond line"), P("ULI", "one"), P("ULI", "two")])).Markdown;
        var html = Markdig.Markdown.ToHtml(markdown, CommonMark);
        Assert.Matches("<blockquote>\\s*<p>first line<br />\\s*second line</p>", html);
        Assert.Equal(2, Regex.Matches(html, "<li>").Count);
    }

    [Fact]
    public void ARenderedDocument_IsIdempotent()
    {
        var post = PostWith([P("P", "text *with* stars"), P("H2", "Heading #")]);
        Assert.Equal(Render(post).Markdown, Render(post).Markdown);
        var renderer = new MarkdownRenderer(new RenderOptions(IncludeFrontmatter: false));
        Assert.Equal(renderer.RenderPost(post).Markdown, renderer.RenderPost(post).Markdown); // state is reset between calls
    }
}
