using Ripium.Markdown;
using Ripium.Medium;

namespace Ripium.Tests;

public class InlineMarkdownTests
{
    private static Markup M(string type, int start, int end, string? href = null, string? anchor = null, string? userId = null) =>
        new() { Type = type, Start = start, End = end, Href = href, AnchorType = anchor, UserId = userId };

    private static string R(string text, params Markup[] markups) => InlineMarkdown.Render(text, markups);

    [Fact]
    public void PlainText_IsReturnedUnchanged() => Assert.Equal("Hello world", R("Hello world"));

    [Fact]
    public void EmptyOrWhitespace_IsEmpty()
    {
        Assert.Equal("", R(""));
        Assert.Equal("", R("  \n "));
        Assert.Equal("", InlineMarkdown.Render(null, null));
    }

    [Fact]
    public void Bold_Italic_Code()
    {
        Assert.Equal("a **bold** b", R("a bold b", M("STRONG", 2, 6)));
        Assert.Equal("a *it* b", R("a it b", M("EM", 2, 4)));
        Assert.Equal("run `ls -la` now", R("run ls -la now", M("CODE", 4, 10)));
    }

    [Fact]
    public void Link_And_UserLink()
    {
        Assert.Equal("see [docs](https://x.io/a) ok", R("see docs ok", M("A", 4, 8, "https://x.io/a", "LINK")));
        Assert.Equal("[@bob](https://medium.com/u/abc123)", R("@bob", M("A", 0, 4, anchor: "USER", userId: "abc123")));
    }

    [Fact]
    public void LinkDestination_IsMadeSafe()
    {
        Assert.Equal("[x](https://en.wikipedia.org/wiki/Foo_%28bar%29)",
            R("x", M("A", 0, 1, "https://en.wikipedia.org/wiki/Foo_\\(bar\\)", "LINK")));
        Assert.Equal("[x](https://a.io/a%20b)", R("x", M("A", 0, 1, "https://a.io/a b", "LINK")));
        Assert.Equal("[x](https://a.io/z)", R("x", M("A", 0, 1, "https://a.io/z\\", "LINK")));
    }

    [Fact]
    public void BoldAndItalicOnSameRange_NestsCleanly() =>
        Assert.Equal("***both***", R("both", M("STRONG", 0, 4), M("EM", 0, 4)));

    [Fact]
    public void Link_WrapsFormatting_RegardlessOfMarkupOrder()
    {
        var link = M("A", 0, 9, "https://x.io", "LINK");
        var strong = M("STRONG", 0, 9);
        Assert.Equal("[**bold link**](https://x.io)", R("bold link", link, strong));
        Assert.Equal("[**bold link**](https://x.io)", R("bold link", strong, link));
    }

    [Fact]
    public void WhitespaceInsideEmphasisIsMovedOutside() =>
        Assert.Equal("a **b** c", R("a b c", M("STRONG", 1, 4))); // range " b " is trimmed to "b"

    [Fact]
    public void PunctuationOnlyEmphasisIsDropped() =>
        Assert.Equal("wait ... what", R("wait ... what", M("STRONG", 5, 8)));

    [Fact]
    public void PartialOverlap_ClosesAndReopensWithoutBlanksInsideDelimiters()
    {
        // STRONG over "aaaaa bbbbb", EM over "bbbbb ccccc": the shared word is both. Reopening the italics must not
        // leave "* ccccc" (an opener followed by a blank is not emphasis), so the blank moves outside.
        var result = R("aaaaa bbbbb ccccc", M("STRONG", 0, 11), M("EM", 6, 17));
        Assert.Equal("**aaaaa *bbbbb*** *ccccc*", result);
    }

    [Fact]
    public void BoldPartiallyOverlappingLink_NestsByClosingAndReopening() =>
        Assert.Equal("[a**b**](https://x.io) **c**",
            R("ab c", M("A", 0, 2, "https://x.io", "LINK"), M("STRONG", 1, 4)));

    [Fact]
    public void CodeStaysInnermost_WhenEmphasisStartsInsideIt() =>
        // emphasis markers inside an open code span would be literal text, so code is closed and reopened
        Assert.Equal("`a` *`b` c*", R("a b c", M("CODE", 0, 3), M("EM", 2, 5)));

    [Fact]
    public void EmphasisThatCannotFlankACodeSpan_IsDroppedInsteadOfLeaking() =>
        // "a*`b`*" cannot open or close: a letter touches the delimiter and a backtick sits inside it
        Assert.Equal("`ab` c", R("ab c", M("CODE", 0, 2), M("EM", 1, 4)));

    [Fact]
    public void EmphasisWithSameRangeAsCode_IsDropped() =>
        Assert.Equal("`ab`", R("ab", M("CODE", 0, 2), M("EM", 0, 2)));

    [Fact]
    public void CodeWithSameRangeAsLink_LosesToLink() =>
        Assert.Equal("[ab](https://x.io)", R("ab", M("CODE", 0, 2), M("A", 0, 2, "https://x.io", "LINK")));

    [Fact]
    public void DuplicateMarkups_AreCollapsed() =>
        Assert.Equal("**x**", R("x", M("STRONG", 0, 1), M("STRONG", 0, 1)));

    [Fact]
    public void Code_IsNotEscaped_AndHandlesBackticks()
    {
        Assert.Equal("`a_b*c[1]`", R("a_b*c[1]", M("CODE", 0, 8)));
        Assert.Equal("`` a`b ``", R("a`b", M("CODE", 0, 3)));
    }

    [Theory]
    [InlineData("1 * 2", "1 \\* 2")]
    [InlineData("a [b] c", "a \\[b\\] c")]
    [InlineData("x < y", "x \\< y")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("_lead and trail_", "\\_lead and trail\\_")]
    [InlineData("a ~strike~", "a \\~strike\\~")]
    [InlineData("AT&T and &amp; entity", "AT&T and \\&amp; entity")]
    [InlineData("#hashtag", "\\#hashtag")]
    [InlineData("> not a quote", "\\> not a quote")]
    [InlineData("- not a list", "\\- not a list")]
    [InlineData("1. not a list", "1\\. not a list")]
    [InlineData("2024 was great", "2024 was great")]
    [InlineData("-5 degrees", "-5 degrees")]
    public void PlainTextIsEscaped(string text, string expected) => Assert.Equal(expected, R(text));

    [Fact]
    public void Newlines_BecomeHardBreaks() => Assert.Equal("one\\\ntwo", R("one\ntwo"));

    [Fact]
    public void TrailingNewlines_AreTrimmed_AndOffsetsStayAligned()
    {
        Assert.Equal("**x**", R("\n x \n", M("STRONG", 2, 3)));
        Assert.Equal("a\\\nb", R("a\n\n\nb"));
    }

    // ---- UTF-16 offsets -----------------------------------------------------------------------------------

    [Fact]
    public void Offsets_AreUtf16CodeUnits_NotCodePoints()
    {
        // "😀" is two UTF-16 units, so "bold" starts at index 3.
        var text = "😀 bold";
        Assert.Equal(7, text.Length); // 2 + 1 + 4 UTF-16 units, but only 6 code points
        Assert.Equal("😀 **bold**", R(text, M("STRONG", 3, 7)));
    }

    [Fact]
    public void Offsets_SplittingASurrogatePair_AreWidened()
    {
        // end offset 1 lands between the two halves of the emoji; the whole emoji must be kept together.
        // (An emoji has no letters or digits, so the bold itself is dropped, but the text must stay intact.)
        Assert.Equal("😀 x", R("😀 x", M("STRONG", 0, 1)));
        Assert.Equal("**😀a** x", R("😀a x", M("STRONG", 1, 3))); // start offset inside the pair is widened to 0
    }

    [Fact]
    public void OutOfRangeOffsets_AreClamped()
    {
        Assert.Equal("**abc**", R("abc", M("STRONG", -5, 99)));
        Assert.Equal("abc", R("abc", M("STRONG", 7, 9)));
        Assert.Equal("abc", R("abc", M("STRONG", 2, 1)));
    }

    [Fact]
    public void IgnoreEm_DropsItalics() =>
        Assert.Equal("a **b** c", InlineMarkdown.Render("a b c", [M("EM", 0, 5), M("STRONG", 2, 3)], ignoreEm: true));

    [Fact]
    public void UnknownMarkupTypes_AreIgnored() => Assert.Equal("text", R("text", M("HIGHLIGHT", 0, 4)));

    [Fact]
    public void LinkWithoutHref_IsIgnored() => Assert.Equal("text", R("text", M("A", 0, 4, null, "LINK")));

    [Fact]
    public void EscapeText_CollapsesNewlines() => Assert.Equal("a b", InlineMarkdown.EscapeText("a\nb"));

    // ---- characters other tools act on ---------------------------------------------------------------------

    [Theory]
    [InlineData("costs $5 and $10", "costs \\$5 and \\$10")] // $...$ is math in Obsidian and GitHub and would swallow the text between
    [InlineData("a == b", "a \\=\\= b")] // ==x== is a highlight in Obsidian
    [InlineData("a = b", "a = b")]
    [InlineData("x %% y", "x \\%\\% y")] // %%x%% is a hidden comment in Obsidian
    [InlineData("100% sure", "100% sure")]
    [InlineData("a ~~b~~", "a \\~\\~b\\~\\~")]
    [InlineData("AT&T and &amp; and &#169; and &copy", "AT&T and \\&amp; and \\&#169; and &copy")]
    [InlineData("x < y > z", "x \\< y > z")]
    [InlineData("a [b] c", "a \\[b\\] c")]
    [InlineData("see! more", "see! more")]
    public void PlainTextEscapes_ForCharactersOtherToolsAct_On(string text, string expected) => Assert.Equal(expected, R(text));

    [Theory]
    [InlineData("b. item", "b\\. item")]
    [InlineData("IV) item", "IV\\) item")]
    [InlineData("A. Intro", "A\\. Intro")]
    [InlineData("1) one", "1\\) one")]
    [InlineData("12. twelve", "12\\. twelve")]
    [InlineData("Dr. Smith", "Dr. Smith")] // not a marker: "r" is no roman numeral
    [InlineData("Version 2. later", "Version 2. later")] // mid-line
    [InlineData("12345678901. too many digits", "12345678901. too many digits")]
    [InlineData("1.5 liters", "1.5 liters")]
    [InlineData("b.c", "b.c")]
    [InlineData(": definition", "\\: definition")]
    [InlineData("::: note", "\\::: note")]
    [InlineData("| a | b |", "\\| a | b |")]
    [InlineData("+--+--+", "\\+--+--+")]
    [InlineData("a | b", "a | b")]
    public void LineStartConstructs_AreNeutralised_OnlyAtTheStartOfALine(string text, string expected) => Assert.Equal(expected, R(text));

    [Fact]
    public void ABangBeforeALink_IsEscaped_SoItDoesNotBecomeAnImage() =>
        Assert.Equal("hi\\![link](https://x.io/a)", R("hi!link", M("A", 3, 7, "https://x.io/a", "LINK")));

    [Fact]
    public void CodeNewlines_BecomeSpaces_BecauseBlockSyntaxCouldOtherwiseClaimTheNextLine() =>
        Assert.Equal("`a # b`", R("a\n# b", M("CODE", 0, 5)));

    [Fact]
    public void ExtremeOffsets_AreClampedWithoutOverflow()
    {
        Assert.Equal("**abc**", R("abc", M("STRONG", int.MinValue, int.MaxValue)));
        Assert.Equal("abc", R("abc", M("STRONG", int.MaxValue, int.MaxValue)));
        Assert.Equal("abc", R("abc", M("STRONG", int.MinValue, int.MinValue)));
        Assert.Equal("abc", R("abc", new Markup { Type = "STRONG", Start = null, End = 2 }));
    }

    [Fact]
    public void NullMarkupEntries_AreIgnored() => Assert.Equal("a", InlineMarkdown.Render("a", [null!]));

    [Fact]
    public void LinkTextWithBrackets_IsEscaped() =>
        Assert.Equal("[a \\[b\\] c](https://x.io/a)", R("a [b] c", M("A", 0, 7, "https://x.io/a", "LINK")));

    [Fact]
    public void UserLinkWithoutAnId_FallsBackToItsHref() =>
        Assert.Equal("[@bob](https://x.io/u)", R("@bob", new Markup { Type = "A", Start = 0, End = 4, Href = "https://x.io/u", AnchorType = "USER" }));

    // ---- URL safety ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://medium.com/p/1", true)]
    [InlineData("HTTP://EXAMPLE.COM", true)]
    [InlineData("mailto:a@b.io", true)]
    [InlineData("tel:+123", true)]
    [InlineData("/relative/path", true)]
    [InlineData("//cdn.example.com/x", true)]
    [InlineData("#fragment", true)]
    [InlineData("path/with:colon", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt:alert(1)", false)]
    [InlineData("  javascript:alert(1)", false)]
    [InlineData("data:text/html;base64,AAAA", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("obsidian://open?vault=x", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeUrl(string url, bool expected) => Assert.Equal(expected, InlineMarkdown.IsSafeUrl(url));

    [Theory]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("java\u0001script:alert(1)")]
    [InlineData("\u0001javascript:alert(1)")]
    public void ControlCharactersCannotHideAScheme(string href)
    {
        // browsers ignore control characters inside a scheme, so FormatDestination must remove them before the check
        Assert.False(InlineMarkdown.IsSafeUrl(InlineMarkdown.FormatDestination(href)));
        Assert.Equal("click", R("click", M("A", 0, 5, href, "LINK")));
    }

    [Fact]
    public void UnsafeLinks_RenderAsPlainTextWithTheirOtherFormatting()
    {
        Assert.Equal("click", R("click", M("A", 0, 5, "javascript:alert(1)", "LINK")));
        Assert.Equal("**click**", R("click", M("A", 0, 5, "javascript:alert(1)", "LINK"), M("STRONG", 0, 5)));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Why I love C #", "Why I love C \\#")]
    [InlineData("Why I love C ##", "Why I love C \\##")]
    [InlineData("C#", "C#")]
    [InlineData("a # b", "a # b")]
    [InlineData("Plain", "Plain")]
    public void EscapeAtxClosingSequence(string heading, string expected) =>
        Assert.Equal(expected, InlineMarkdown.EscapeAtxClosingSequence(heading));

    [Theory]
    [InlineData("hello", 1, 4, "ell")]
    [InlineData("hello", -5, 99, "hello")]
    [InlineData("hello", 4, 2, "")]
    public void Slice(string text, int start, int end, string expected) => Assert.Equal(expected, InlineMarkdown.Slice(text, start, end));

    [Fact]
    public void Slice_NeverSplitsASurrogatePair()
    {
        Assert.Equal("\U0001F600", InlineMarkdown.Slice("\U0001F600x", 1, 1)); // both ends inside the pair: widened to it
        Assert.Equal("\U0001F600", InlineMarkdown.Slice("\U0001F600x", 0, 1));
        Assert.Equal("", InlineMarkdown.Slice("abc", null, 2));
    }

    [Fact]
    public void EscapeText_EscapesLineStartSyntaxToo() =>
        Assert.Equal("\\# not a heading", InlineMarkdown.EscapeText("# not a heading"));
}
