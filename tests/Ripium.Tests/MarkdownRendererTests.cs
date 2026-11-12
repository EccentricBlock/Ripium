using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ripium.Markdown;
using Ripium.Medium;

namespace Ripium.Tests;

public class MarkdownRendererTests
{
    // ---- helpers --------------------------------------------------------------------------------------------

    private static Paragraph P(string type, string? text = null, params Markup[] markups) =>
        new() { Type = type, Text = text, Markups = markups.ToList() };

    private static Post PostWith(IEnumerable<Paragraph> paragraphs, string title = "A Title", string? subtitle = null, string[]? tags = null) => new()
    {
        Title = title,
        PreviewContent = new PreviewContent { Subtitle = subtitle },
        Tags = tags?.Select(t => new Tag { DisplayTitle = t }).ToList(),
        Content = new PostContent { BodyModel = new BodyModel { Paragraphs = paragraphs.ToList() } },
    };

    private static string Body(params Paragraph[] paragraphs) => BodyOf(PostWith(paragraphs));

    private static string BodyOf(Post post)
    {
        var md = MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown;
        // drop the "# Title" block (and byline) that always comes first
        var parts = md.TrimEnd('\n').Split("\n\n");
        return string.Join("\n\n", parts.Skip(1));
    }

    private static RenderResult Render(Post post, bool frontmatter = false, int width = 1400) =>
        MarkdownRenderer.Render(post, new RenderOptions(width, frontmatter));

    // ---- blocks ----------------------------------------------------------------------------------------------

    [Fact]
    public void Headings_MapToH2_H3_H4_AndEmptyOnesAreSkipped() =>
        Assert.Equal("## Two\n\n### Three\n\n#### Four", Body(P("H2", "Two"), P("H3", "Three"), P("H4", "Four"), P("H3", "  ")));

    [Fact]
    public void Paragraphs_AreSeparatedByOneBlankLine() =>
        Assert.Equal("one\n\ntwo", Body(P("P", "one"), P("P", ""), P("P", "two")));

    [Fact]
    public void UnorderedList_GroupsConsecutiveItems_AndIndentsContinuationLines() =>
        Assert.Equal("- a\n- b\\\n  more\n\nafter", Body(P("ULI", "a"), P("ULI", "b\nmore"), P("P", "after")));

    [Fact]
    public void OrderedList_IsRenumberedFromOnePerGroup() =>
        Assert.Equal("1. a\n2. b\n\nbreak\n\n1. c", Body(P("OLI", "a"), P("OLI", "b"), P("P", "break"), P("OLI", "c")));

    [Fact]
    public void AdjacentDifferentLists_AreSeparate() =>
        Assert.Equal("- a\n\n1. b", Body(P("ULI", "a"), P("OLI", "b")));

    [Fact]
    public void ConsecutivePre_MergeIntoOneFencedBlock_WithLanguage()
    {
        var first = P("PRE", "let a = 1;");
        first.CodeBlockMetadata = new CodeBlockMetadata { Lang = "javascript" };
        Assert.Equal("```javascript\nlet a = 1;\nlet b = 2;\n```", Body(first, P("PRE", "let b = 2;")));
    }

    [Fact]
    public void Pre_WithoutLanguage_HasPlainFence_AndIgnoresMarkups() =>
        Assert.Equal("```\nbold stays plain\n```", Body(P("PRE", "bold stays plain", new Markup { Type = "STRONG", Start = 0, End = 4 })));

    [Fact]
    public void Pre_PreservesBlankLinesAndIndentation_AndLengthensFenceForBackticks() =>
        Assert.Equal("````\n```\n  x\n\n\ny\n```\n````", Body(P("PRE", "```\n  x\n\n\ny\n```")));

    [Fact]
    public void BlankLinesInsideCode_AreNotCollapsedByTheDocumentAssembly()
    {
        var md = Render(PostWith([P("P", "before"), P("PRE", "a\n\n\n\nb"), P("P", "after")])).Markdown;
        Assert.Contains("a\n\n\n\nb", md);
    }

    [Fact]
    public void Blockquote_PrefixesEveryLine_AndPullQuoteIsItalic()
    {
        Assert.Equal("> one\\\n> two", Body(P("BQ", "one\ntwo")));
        Assert.Equal("> *pulled*", Body(P("PQ", "pulled")));
        Assert.Equal("> *a\\\n> b*", Body(P("PQ", "a\nb"))); // one emphasis across the hard break; a link may cross it
    }

    [Fact]
    public void PullQuote_WithItalicMarkup_DoesNotFlipIntoBold() =>
        Assert.Equal("> *all italic*", Body(P("PQ", "all italic", new Markup { Type = "EM", Start = 0, End = 10 })));

    [Fact]
    public void SectionCaption_IsRenderedAsText_AndUnknownTypesWarn()
    {
        Assert.Equal("by me", Body(P("SECTION_CAPTION", "by me")));
        var result = Render(PostWith([P("MYSTERY", "kept")]));
        Assert.Contains("kept", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("MYSTERY"));
    }

    // ---- images, mixtapes, embeds ---------------------------------------------------------------------------

    private static Paragraph Img(string id, string? alt = null, string? caption = null, string? href = null) => new()
    {
        Type = "IMG", Text = caption, Href = href,
        Metadata = new ImageMetadata { Id = id, Alt = alt },
    };

    [Fact]
    public void Image_UsesMiroUrl_AndCaptionIsItalic()
    {
        var md = Body(Img("1*abc.jpeg", "A [cat]", "Photo by me"));
        Assert.Equal("![A \\[cat\\]](https://miro.medium.com/v2/resize:fit:1400/1*abc.jpeg)\n\n*Photo by me*", md);
    }

    [Fact]
    public void Image_Width_IsConfigurable_AndImagesAreCollectedOnce()
    {
        var result = Render(PostWith([Img("1*a.png"), Img("1*b.png"), Img("1*a.png")]), width: 700);
        Assert.Equal(["https://miro.medium.com/v2/resize:fit:700/1*a.png", "https://miro.medium.com/v2/resize:fit:700/1*b.png"], result.ImageUrls);
    }

    [Fact]
    public void LinkedImage_IsWrappedInALink() =>
        Assert.Equal("[![](https://miro.medium.com/v2/resize:fit:1400/1*a.png)](https://insta.example/p/1)", Body(Img("1*a.png", href: "https://insta.example/p/1")));

    [Fact]
    public void ImageWithoutId_IsSkipped() => Assert.Equal("", Body(new Paragraph { Type = "IMG", Metadata = new ImageMetadata() }));

    [Fact]
    public void ImageRows_RenderAsConsecutiveImages()
    {
        static Paragraph Row(string id, string layout) => new() { Type = "IMG", Metadata = new ImageMetadata { Id = id } };
        var md = Body(Row("1*a.png", "OUTSET_ROW"), Row("1*b.png", "OUTSET_ROW_CONTINUE"));
        Assert.Equal(2, Regex.Matches(md, @"!\[\]").Count);
    }

    private static Paragraph Mixtape(string text, string href, params Markup[] markups) =>
        new() { Type = "MIXTAPE_EMBED", Text = text, Markups = markups.ToList(), MixtapeMetadata = new MixtapeMetadata { Href = href } };

    [Fact]
    public void Mixtape_UsesBoldRangeAsTitle_AndItalicRangeAsDescription()
    {
        var text = "My Title\nA longer description…example.com";
        var md = Body(Mixtape(text, "https://example.com/post",
            new Markup { Type = "A", Start = 0, End = text.Length, Href = "https://example.com/post" },
            new Markup { Type = "STRONG", Start = 0, End = 8 },
            new Markup { Type = "EM", Start = 9, End = 29 }));
        Assert.Equal("> [**My Title**](https://example.com/post)\\\n> A longer description", md);
    }

    [Fact]
    public void Mixtape_WithoutDescription_WhenBoldAndItalicShareARange()
    {
        var text = "Monolingualingmarstein.github.io";
        var md = Body(Mixtape(text, "https://x.io/m",
            new Markup { Type = "A", Start = 0, End = text.Length, Href = "https://x.io/m" },
            new Markup { Type = "STRONG", Start = 0, End = 11 },
            new Markup { Type = "EM", Start = 0, End = 11 }));
        Assert.Equal("> [**Monolingual**](https://x.io/m)", md);
    }

    [Fact]
    public void Mixtape_WithoutMarkups_FallsBackToFirstTwoLines() =>
        Assert.Equal("> [**Title**](https://x.io)\\\n> Desc", Body(Mixtape("Title\nDesc", "https://x.io")));

    [Fact]
    public void Mixtape_WithoutHref_IsPlainText()
    {
        var p = P("MIXTAPE_EMBED", "just text");
        Assert.Equal("just text", Body(p));
    }

    private static Paragraph Iframe(string? id, string? src = null, string? title = null) => new()
    {
        Type = "IFRAME",
        Iframe = new IframeData { MediaResource = new MediaResource { Id = id, IframeSrc = src, Title = title } },
    };

    [Fact]
    public void Iframe_LinksToMediumMediaPage_WhenThereIsNoSource() =>
        Assert.Equal("[Embedded content: Hilal on Twitter](https://medium.com/media/abc123)", Body(Iframe("abc123", "", "Hilal on Twitter")));

    [Fact]
    public void Iframe_PrefersExplicitSource_AndUnwrapsEmbedly()
    {
        Assert.Equal("[Embedded content](https://player.example/v/1)", Body(Iframe("abc", "https://player.example/v/1")));
        var embedly = "https://cdn.embedly.com/widgets/media.html?src=https%3A%2F%2Fwww.youtube.com%2Fembed%2FX&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DX&type=text%2Fhtml";
        Assert.Equal("[Embedded content](https://www.youtube.com/watch?v=X)", Body(Iframe("abc", embedly)));
    }

    [Fact]
    public void Iframe_WithNothingToLinkTo_IsSkipped() => Assert.Equal("", Body(Iframe(null)));

    // ---- leading-paragraph cleanup ---------------------------------------------------------------------------

    [Fact]
    public void TitleRepeatedAsHeading_IsDropped_ButOnlyNearTheTop()
    {
        var paragraphs = new[] { P("H3", "A Title"), P("P", "p1"), P("P", "p2"), P("P", "p3"), P("P", "p4"), P("H3", "A Title") };
        var md = Render(PostWith(paragraphs)).Markdown;
        Assert.Single(Regex.Matches(md, "^# A Title$", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(md, "^### A Title$", RegexOptions.Multiline)); // the late one stays
    }

    [Fact]
    public void FuzzyTitleMatch_Drops_ButDifferentHeadingStays()
    {
        // "A Title!" vs "A Title" has ratio 0.93; the threshold is strictly above 0.80
        var md = Render(PostWith([P("H3", "A Title!"), P("H3", "Something else entirely")], title: "A Title")).Markdown;
        Assert.DoesNotContain("A Title!", md);
        Assert.Contains("### Something else entirely", md);
    }

    [Fact]
    public void TruncatedTitle_IsCompletedFromTheBody()
    {
        var result = Render(PostWith([P("H3", "A very long title that Medium truncated in metadata")], title: "A very long title that Medium…"));
        Assert.Equal("A very long title that Medium truncated in metadata", result.Title);
        Assert.StartsWith("# A very long title that Medium truncated in metadata", result.Markdown);
    }

    [Fact]
    public void Subtitle_IsShownOnce_AndItsParagraphDropped()
    {
        var md = Render(PostWith([P("H3", "A Title"), P("P", "The subtitle."), P("P", "Body")], subtitle: "The subtitle.")).Markdown;
        Assert.Contains("*The subtitle.*", md);
        Assert.Single(Regex.Matches(md, "The subtitle"));
    }

    [Fact]
    public void TruncatedSubtitle_IsDropped_AndTheFullParagraphKept()
    {
        var md = Render(PostWith([P("P", "A long opening paragraph that the preview cut off, with plenty more words after it.")],
            subtitle: "A long opening paragraph that the preview cut…")).Markdown;
        Assert.DoesNotContain("*A long opening", md);
        Assert.Contains("A long opening paragraph that the preview cut off, with plenty more words after it.", md);
    }

    [Fact]
    public void TagHeadings_AreDroppedOnlyAtTheTop()
    {
        var md = Render(PostWith([P("H4", "Python"), P("P", "x"), P("P", "x"), P("P", "x"), P("P", "x"), P("H4", "Python")], tags: ["Python"])).Markdown;
        Assert.Single(Regex.Matches(md, "^#### Python$", RegexOptions.Multiline));
    }

    // ---- document shape -------------------------------------------------------------------------------------

    [Fact]
    public void Frontmatter_IsEmittedFirst_WithAuthorTagsAndDates()
    {
        var post = PostWith([P("P", "Body")], title: "T: \"quoted\"", tags: ["One", "Two"]);
        post.Creator = new Creator { Name = "Ada", Username = "ada" };
        post.Collection = new Collection { Name = "Pub" };
        post.FirstPublishedAt = 1676317725922;
        post.ReadingTime = 4.2;
        post.CanonicalUrl = "https://medium.com/@ada/t-abc123abc123";
        post.PreviewImage = new ImageRef { Id = "1*p.png" };

        var md = Render(post, frontmatter: true).Markdown;
        Assert.StartsWith("---\ntitle: \"T: \\\"quoted\\\"\"\nauthor: \"Ada\"\nauthor_url: \"https://medium.com/@ada\"\npublication: \"Pub\"\npublished: 2023-02-13T19:48:45Z\nreading_time: 5\nurl: \"https://medium.com/@ada/t-abc123abc123\"\ntags:\n  - \"One\"\n  - \"Two\"\npreview_image: \"https://miro.medium.com/v2/resize:fit:1400/1*p.png\"\n---\n\n# T: \"quoted\"\n", md);
    }

    [Fact]
    public void PreviewImage_IsTrackedForDownload_SoTheFrontmatterCanLinkToTheLocalCopy()
    {
        var post = PostWith([P("P", "x")]);
        post.PreviewImage = new ImageRef { Id = "1*p.png" };
        Assert.Equal(["https://miro.medium.com/v2/resize:fit:1400/1*p.png"], Render(post, frontmatter: true).ImageUrls);
        Assert.Empty(Render(post, frontmatter: false).ImageUrls); // not written without frontmatter
    }

    [Fact]
    public void WithoutFrontmatter_ABylinePrecedesTheBody()
    {
        var post = PostWith([P("P", "Body")]);
        post.Creator = new Creator { Name = "Ada" };
        post.FirstPublishedAt = 1676317725922;
        post.ReadingTime = 4.2;
        Assert.Equal("# A Title\n\n*By Ada · February 13, 2023 · 5 min read*\n\nBody\n", Render(post).Markdown);
    }

    [Fact]
    public void EmptyPost_StillProducesATitle() =>
        Assert.Equal("# A Title\n", Render(PostWith([])).Markdown);

    [Fact]
    public void TitleWithMarkdownSpecials_IsEscaped() =>
        Assert.Equal("# \\[WIP\\] 5 \\* 3\n", Render(PostWith([], title: "[WIP] 5 * 3")).Markdown);

    // ---- real articles (fixtures captured from Medium) ---------------------------------------------------------

    public static TheoryData<string> Fixtures() =>
        new() { "27832c8f6644", "252990763468", "4e4017b89905", "9dc802a10618", "d4222bd1a0b3" };

    private static Post LoadFixture(string id)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"post_{id}.json"));
        return Post.FromJson(JsonNode.Parse(json)!);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RealArticles_ProduceWellFormedMarkdown(string id)
    {
        var post = LoadFixture(id);
        var result = Render(post, frontmatter: true);
        var md = result.Markdown;

        Assert.DoesNotContain(result.Warnings, w => !w.Contains("Unsupported"));
        Assert.StartsWith("---\n", md);
        Assert.EndsWith("\n", md);
        Assert.DoesNotContain("\n\n\n", Regex.Replace(md, "(?s)```.*?```", "CODE")); // no runs of blank lines outside code
        var prose = Regex.Replace(md, "(?s)```.*?```", "CODE"); // '# comment' lines inside code are not headings
        Assert.Single(Regex.Matches(prose, "^# ", RegexOptions.Multiline)); // exactly one H1, prose headings start at H2
        Assert.False(Regex.IsMatch(md, @"(?<!\\)\*\*\*\*"), "adjacent emphasis delimiters"); // `****` only when escaped
        Assert.Equal(0, md.Split('\n').Count(l => l.StartsWith("```")) % 2); // fences are balanced
        Assert.All(result.ImageUrls, url => Assert.Contains($"]({url})", md));
    }

    [Fact]
    public void Fixture_StopWastingYourLife_HasTitleSubtitleThenHeroImage()
    {
        var md = Render(LoadFixture("27832c8f6644"), frontmatter: true).Markdown;
        Assert.Contains("\n---\n\n# Stop Wasting Your Life\n\n*I want you to use your imagination for a second.*\n\n![](https://miro.medium.com/v2/resize:fit:1400/1*g6_rwCXG8HuOBKVI-NM3Qw.jpeg)\n\nI don’t know who you are", md);
        var body = md[(md.IndexOf("\n---\n\n", StringComparison.Ordinal) + 5)..];
        Assert.Single(Regex.Matches(body, "I want you to use your imagination")); // the subtitle paragraph was not repeated
    }

    [Fact]
    public void Fixture_NeuralProphet_HasLinkCardsAndUnwrappedYouTubeEmbed()
    {
        var md = Render(LoadFixture("252990763468")).Markdown;
        Assert.Contains("> [**Benchmarking Facebook Prophet**](https://valeman.medium.com/benchmarking-facebook-prophet-53273c3ee9c6)", md);
        Assert.Contains("(https://www.youtube.com/watch?v=0vZpb9wFPx0)", md);
        Assert.DoesNotContain("embedly", md);
        Assert.Contains("1. ", md);
    }

    [Fact]
    public void Fixture_GrowYourBlog_KeepsImageRowsAndEmbeds()
    {
        var md = Render(LoadFixture("4e4017b89905")).Markdown;
        Assert.Contains("[Embedded content: Hilal on Twitter](https://medium.com/media/51190cfe76c35f6c558b8c3245dc6198)", md);
        Assert.Contains("](http://facebook.com/alimeseu)", md); // linked image
    }

    [Fact]
    public void Fixture_LinuxCommands_HasFencedCodeAndInlineCode()
    {
        var md = Render(LoadFixture("9dc802a10618")).Markdown;
        Assert.Contains("```", md);
        Assert.Matches("`[^`\n]+`", md);
    }

    [Fact]
    public void Fixture_Emoji_BoldSpansEndExactlyAfterTheirEmoji()
    {
        // Offsets are UTF-16 code units; a code-point based implementation would shift these spans by one per emoji.
        var md = Render(LoadFixture("d4222bd1a0b3")).Markdown;
        Assert.Contains("**This is how my hunt started on this website for vulnerabilities😈😈**", md);
        Assert.Contains("**Then after coming out from trance🤣🤣🤣 and noticed that the site wasn’t using any hashing or encryption for the parameters😮😮🙀🙀.**", md);
    }
}
