using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Ripium.Markdown;
using Ripium.Medium;

namespace Ripium.Tests;

/// <summary>
/// Renders text through <see cref="InlineMarkdown"/>, parses the result with a real Markdown parser and checks that
/// the visible text is exactly the original. This is the strongest check that the output is valid Markdown: a leaked
/// delimiter, an eaten character or an accidental block construct all change the visible text.
/// </summary>
public class InlineRoundTripTests
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();
    // Markdig's "advanced" set (tables, definition lists, containers, math, extra emphasis, alpha lists, ...) is the
    // harshest realistic target. Known limitation: Markdig-only "++inserted++", "^superscript^", ""citation"" and ::inline container:: are not escaped, because
    // that would turn every "C++" into "C\+\+" for the benefit of one parser.
    private static readonly MarkdownPipeline Gfm = new MarkdownPipelineBuilder()
        .UseAbbreviations().UseAutoIdentifiers().UseDefinitionLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough | Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Marked)
        .UseFigures().UseFooters().UseFootnotes().UseGridTables().UseMathematics().UseMediaLinks().UsePipeTables()
        .UseListExtras().UseTaskLists().UseDiagrams().UseAutoLinks().UseGenericAttributes()
        .Build();

    private static Markup M(string type, int start, int end, string? href = null) =>
        new() { Type = type, Start = start, End = end, Href = href, AnchorType = type == "A" ? "LINK" : null };

    private static string Visible(string markdown, MarkdownPipeline pipeline) =>
        Collapse(Markdig.Markdown.ToPlainText(markdown, pipeline));

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string? Check(string text, IReadOnlyList<Markup> markups, bool noLiteralDelimiters)
    {
        var rendered = InlineMarkdown.Render(text, markups);

        // text with nothing visible (only blanks, zero-width characters) is dropped on purpose
        if (!HasVisibleText(text))
            return rendered.Length == 0 ? null : $"invisible text {Show(text)} was rendered as {Show(rendered)}";

        foreach (var (name, pipeline) in new[] { ("CommonMark", CommonMark), ("GFM", Gfm) })
        {
            var visible = Visible(rendered, pipeline);
            if (visible != Collapse(InlineMarkdown.ReplaceControlCharacters(text)))
                return $"[{name}] visible text differs\n  text:     {Show(text)}\n  markups:  {Describe(markups)}\n  rendered: {Show(rendered)}\n  visible:  {Show(visible)}";
            if (noLiteralDelimiters && visible.IndexOfAny(['*', '_', '`', '~', '[', ']', '\\']) >= 0)
                return $"[{name}] a delimiter leaked into the visible text\n  text:     {Show(text)}\n  markups:  {Describe(markups)}\n  rendered: {Show(rendered)}\n  visible:  {Show(visible)}";
        }
        return null;
    }

    // Failures involve invisible and combining characters, so anything that is not printable ASCII is shown as a code point.
    private static string Show(string s) =>
        "\"" + string.Concat(s.EnumerateRunes().Select(r => r.Value switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            >= 0x20 and < 0x7F => r.ToString(),
            _ => $"<U+{r.Value:X4}>",
        })) + "\"";

    private static string Describe(IEnumerable<Markup> markups) =>
        string.Join(", ", markups.Select(m => $"{m.Type}[{m.Start},{m.End})"));

    // ---- the reviewer's concrete repros, kept as named regressions ------------------------------------------

    [Fact]
    public void BoldInsideItalic_ThenComma_DoesNotLeakAsterisks()
    {
        var markups = new[] { M("EM", 0, 9), M("STRONG", 0, 3) };
        Assert.Equal("***Hey**, wait*", InlineMarkdown.Render("Hey, wait", markups));
        Assert.Null(Check("Hey, wait", markups, noLiteralDelimiters: true));
    }

    [Fact]
    public void BoldAtStartOfItalic_IsNotSplitIntoTwoItalics() =>
        Assert.Equal("***foo**bar*", InlineMarkdown.Render("foobar", [M("EM", 0, 6), M("STRONG", 0, 3)]));

    [Theory]
    [InlineData("foo\n  - bar")]
    [InlineData("foo\n  # bar")]
    [InlineData("foo\n  > bar")]
    [InlineData("foo\n===")]
    [InlineData("foo\n  ---")]
    [InlineData("foo\n1. bar")]
    [InlineData("foo\n   2) bar")]
    [InlineData("foo\n+ bar")]
    public void BlockSyntaxAfterALineBreak_StaysPlainText(string text) =>
        Assert.Null(Check(text, [], noLiteralDelimiters: false));

    [Fact]
    public void CodeSpanContent_IsNeverEscaped()
    {
        var rendered = InlineMarkdown.Render("a\n# b", [M("CODE", 0, 5)]);
        Assert.DoesNotContain("\\", rendered);
        Assert.Null(Check("a\n# b", [M("CODE", 0, 5)], noLiteralDelimiters: false));
    }

    [Fact]
    public void EmphasisBesideQuotesNextToLetters_MovesTheOpeningQuoteOutside()
    {
        // "**" after a letter and before a quote is not an opener in CommonMark, so the opening quote moves out.
        // The closing quote can stay inside because whitespace follows it.
        var markups = new[] { M("STRONG", 3, 8) };
        Assert.Equal("foo\"**bar\"** baz", InlineMarkdown.Render("foo\"bar\" baz", markups));
        Assert.Null(Check("foo\"bar\" baz", markups, noLiteralDelimiters: true));
    }

    [Fact]
    public void UnderscoresBesideEmittedDelimiters_StayLiteral()
    {
        var markups = new[] { M("STRONG", 0, 1), M("EM", 2, 3), M("EM", 4, 5) };
        Assert.Null(Check("e_c_b", markups, noLiteralDelimiters: false));
    }

    [Fact]
    public void OverlappingLinks_AreNeverNested()
    {
        var markups = new[] { M("A", 0, 4, "http://x.io/a"), M("A", 2, 6, "http://y.io/b") };
        Assert.Equal("[abcd](http://x.io/a)[ef](http://y.io/b)", InlineMarkdown.Render("abcdef", markups));
        Assert.Null(Check("abcdef", markups, noLiteralDelimiters: true));
    }

    [Fact]
    public void LinkInsideAnotherLink_IsDropped() =>
        Assert.Equal("[abcdef](http://x.io/a)",
            InlineMarkdown.Render("abcdef", [M("A", 0, 6, "http://x.io/a"), M("A", 2, 4, "http://y.io/b")]));

    [Fact]
    public void PunctuationOnlyCode_KeepsItsFormatting() =>
        Assert.Equal("use `->` here", InlineMarkdown.Render("use -> here", [M("CODE", 4, 6)]));

    // ---- adversarial fuzz -------------------------------------------------------------------------------------

    // RIPIUM_FUZZ_CASES=200000 dotnet test --filter RandomMarkups   runs a much larger search than the default
    private static readonly int FuzzCases =
        int.TryParse(Environment.GetEnvironmentVariable("RIPIUM_FUZZ_CASES"), out var n) && n > 0 ? n : 2500;

    private const string PlainAlphabet = "abcXY 019.,;:!?\"'()/-\n\t \u00e9\U0001F600\U0001F680";
    private const string HostileAlphabet = "ab 1*_`~[]()<>&#=+-.|!\\\"'\n  \U0001F600";

    // no-break space, ideographic space, zero-width space, combining acute, Devanagari with combining marks, emoji and
    // symbols (punctuation under CommonMark 0.31 but not 0.30), a lone CR, characters .NET and CommonMark disagree on, controls
    private const string UnicodeBlanksAndMarks = "\U000000A0\U00003000\U0000200B\U00000301\U00000939\U00000948\U00000902\U0001F600\U00002705\U0000FF01\r"
        + "\U00000085\U00002028\U00002029\U0000000B\U0000000C" // NEL, line/paragraph separator, vertical tab, form feed: .NET calls them white space, CommonMark mostly does not
        + "\U00000000\U0000001B\U0000007F\U00000090"; // NUL, ESC, DEL, a C1 control: replaced by U+FFFD in the output
    private const string UnicodePlainAlphabet = "ab 1.,;:!?\"'()/-\n\t " + UnicodeBlanksAndMarks;
    private const string UnicodeHostileAlphabet = "ab 1*_`~[]()<>&#=+-.|!\\\"'\n  " + UnicodeBlanksAndMarks;

    private static (string Text, List<Markup> Markups) RandomCase(Random rng, string alphabet)
    {
        var length = rng.Next(1, 40);
        var sb = new StringBuilder();
        for (var i = 0; i < length;)
        {
            var take = char.ConvertFromUtf32(char.ConvertToUtf32(alphabet, AlphabetIndex(rng, alphabet)));
            sb.Append(take);
            i++;
        }
        var text = sb.ToString();

        var types = new[] { "STRONG", "EM", "CODE", "A" };
        var markups = new List<Markup>();
        for (var i = rng.Next(0, 6); i > 0; i--)
        {
            var start = rng.Next(-2, text.Length + 2);
            var end = rng.Next(-2, text.Length + 3);
            var type = types[rng.Next(types.Length)];
            markups.Add(M(type, start, end, type == "A" ? (rng.Next(2) == 0 ? "https://x.io/a" : "https://x.io/b(1)") : null));
        }
        return (text, markups);
    }

    private static int AlphabetIndex(Random rng, string alphabet)
    {
        // land on the start of a code point, never in the middle of a surrogate pair
        var i = rng.Next(alphabet.Length);
        return char.IsLowSurrogate(alphabet[i]) ? i - 1 : i;
    }

    [Theory]
    [InlineData(PlainAlphabet, true, 1)]
    [InlineData(PlainAlphabet, true, 2)]
    [InlineData(HostileAlphabet, false, 3)]
    [InlineData(HostileAlphabet, false, 4)]
    [InlineData(UnicodePlainAlphabet, true, 5)]
    [InlineData(UnicodePlainAlphabet, true, 6)]
    [InlineData(UnicodeHostileAlphabet, false, 7)]
    [InlineData(UnicodeHostileAlphabet, false, 8)]
    public void RandomMarkups_RenderToValidMarkdownWithTheSameVisibleText(string alphabet, bool noLiteralDelimiters, int seed)
    {
        var rng = new Random(seed);
        var failures = new List<string>();
        for (var n = 0; n < FuzzCases && failures.Count < 5; n++)
        {
            var (text, markups) = RandomCase(rng, alphabet);
            if (Check(text, markups, noLiteralDelimiters) is { } failure) failures.Add(failure);
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    // ---- every paragraph of the real articles --------------------------------------------------------------------

    public static TheoryData<string> Fixtures() =>
        new() { "27832c8f6644", "252990763468", "4e4017b89905", "9dc802a10618", "d4222bd1a0b3" };

    private static Post LoadFixture(string id) =>
        Post.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"post_{id}.json")))!);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryRealParagraph_KeepsItsVisibleText(string id)
    {
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var p in LoadFixture(id).Paragraphs.Where(p => p.Type is "P" or "BQ" or "PQ" or "ULI" or "OLI" or "H2" or "H3" or "H4"))
        {
            if (string.IsNullOrWhiteSpace(p.Text)) continue;
            checkedCount++;
            if (Check(p.Text!, p.Markups ?? [], noLiteralDelimiters: false) is { } failure && failures.Count < 5)
                failures.Add(failure);
        }
        Assert.True(checkedCount > 10);
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void WholeDocument_ContainsEveryBodyParagraphWhenParsed(string id)
    {
        var post = LoadFixture(id);
        var markdown = MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown;
        var visible = Visible(markdown, Gfm);
        var missing = post.Paragraphs.Skip(4)
            .Where(p => p.Type is "P" or "BQ" or "ULI" or "OLI" && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => Collapse(p.Text!))
            .Where(t => !visible.Contains(t, StringComparison.Ordinal))
            .Take(5)
            .ToList();
        Assert.True(missing.Count == 0, "paragraphs missing from the parsed document:\n" + string.Join("\n", missing));
    }

    // ---- random whole documents ------------------------------------------------------------------------------------

    private static readonly string[] BlockTypes = ["P", "P", "P", "PQ", "BQ", "ULI", "OLI", "H2", "H3", "H4", "IMG"];

    private static bool HasVisibleText(string text) => text.EnumerateRunes().Any(r =>
        !Rune.IsWhiteSpace(r) && Rune.GetUnicodeCategory(r) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control));

    /// <summary>
    /// Random articles built from hostile text: lists, quotes, headings, captions. Every paragraph's text must be visible
    /// in the parsed document. This is what single-paragraph tests cannot see: block constructs, emphasis across line
    /// breaks, indentation after hard breaks, neighbouring blocks.
    /// </summary>
    [Theory]
    [InlineData(HostileAlphabet, 21)]
    [InlineData(UnicodeHostileAlphabet, 22)]
    [InlineData(UnicodePlainAlphabet, 23)]
    public void RandomDocuments_ShowEveryParagraphsTextWhenParsed(string alphabet, int seed)
    {
        var rng = new Random(seed);
        var failures = new List<string>();
        for (var n = 0; n < Math.Max(100, FuzzCases / 8) && failures.Count < 3; n++)
        {
            var paragraphs = new List<Paragraph>();
            for (var i = rng.Next(2, 8); i > 0; i--)
            {
                var (text, markups) = RandomCase(rng, alphabet);
                var type = BlockTypes[rng.Next(BlockTypes.Length)];
                paragraphs.Add(type == "IMG"
                    ? new Paragraph { Type = type, Text = text, Markups = markups, Metadata = new ImageMetadata { Id = "1*a.png", Alt = text } }
                    : new Paragraph { Type = type, Text = text, Markups = markups });
            }
            var post = new Post { Title = "ZZZ a title nothing matches", Content = new PostContent { BodyModel = new BodyModel { Paragraphs = paragraphs } } };
            var markdown = MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown;

            foreach (var (name, pipeline) in new[] { ("CommonMark", CommonMark), ("GFM", Gfm) })
            {
                var visible = Visible(markdown, pipeline);
                var missing = paragraphs.Where(p => HasVisibleText(p.Text!) && !visible.Contains(Collapse(InlineMarkdown.ReplaceControlCharacters(p.Text)), StringComparison.Ordinal)).Take(1).ToList();
                if (missing.Count > 0 && failures.Count < 3)
                    failures.Add($"[{name}] missing: {Show(missing[0].Text!)} ({missing[0].Type}, {Describe(missing[0].Markups!)})\n--- markdown ---\n{markdown}\n--- visible ---\n{visible}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    // ---- feeding an external parser -----------------------------------------------------------------------------------

    /// <summary>
    /// Opt-in: with RIPIUM_ORACLE_EXPORT=&lt;path&gt; writes JSON lines (alphabet, text, markups, rendered) for
    /// tools/markdown-it-oracle.py. Without the variable it does nothing.
    /// </summary>
    [Fact]
    public void ExportCasesForExternalOracle()
    {
        var path = Environment.GetEnvironmentVariable("RIPIUM_ORACLE_EXPORT");
        if (string.IsNullOrEmpty(path)) return;

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        foreach (var (name, alphabet, seed) in new[] { ("plain", PlainAlphabet, 101), ("hostile", HostileAlphabet, 102), ("unicode-plain", UnicodePlainAlphabet, 103), ("unicode-hostile", UnicodeHostileAlphabet, 104) })
        {
            var rng = new Random(seed);
            for (var n = 0; n < FuzzCases; n++)
            {
                var (text, markups) = RandomCase(rng, alphabet);
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    alphabet = name,
                    text,
                    markups = markups.Select(m => $"{m.Type}[{m.Start},{m.End})"),
                    rendered = InlineMarkdown.Render(text, markups),
                }));
            }
        }
    }
}
