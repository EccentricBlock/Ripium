using Ripium.Markdown;
using Ripium.Output;

namespace Ripium.Tests;

public class TextSimilarityTests
{
    [Theory]
    [InlineData("", "", 1.0)]
    [InlineData("abc", "abc", 1.0)]
    [InlineData("abc", "xyz", 0.0)]
    [InlineData("", "abc", 0.0)]
    // values below come from Python: difflib.SequenceMatcher(None, a, b).ratio()
    [InlineData("abcd", "bcde", 0.75)]
    [InlineData("private Thread currentThread;", "private volatile Thread currentThread;", 0.8656716417910447)]
    [InlineData("Stop Wasting Your Life", "Stop wasting your life", 0.8636363636363636)]
    [InlineData("Why you should not ever use expires_in", "Why you should not use expires_in", 0.9295774647887324)]
    public void Ratio_MatchesDifflib(string a, string b, double expected) =>
        Assert.Equal(expected, TextSimilarity.Ratio(a, b), 10);
}

public class FileNameTests
{
    [Theory]
    [InlineData("Stop Wasting Your Life", "stop-wasting-your-life")]
    [InlineData("Why ['1', '5', '11'].map(parseInt) returns [1, NaN, 3] in Javascript", "why-1-5-11-map-parseint-returns-1-nan-3-in-javascript")]
    [InlineData("Café déjà vu — naïve", "cafe-deja-vu-naive")]
    [InlineData("How I Got Free LinkedIn Premium Membership\U0001F923", "how-i-got-free-linkedin-premium-membership")]
    [InlineData("  --Leading and trailing--  ", "leading-and-trailing")]
    [InlineData("C++ vs C# in 2024", "c-vs-c-in-2024")]
    public void Slugify_Latin(string title, string expected) => Assert.Equal(expected, FileNames.Slugify(title, "fallback"));

    [Theory]
    [InlineData("Как я научился", "как-я-научился")] // Cyrillic is kept, lower-cased
    [InlineData("为什么我学会了编程", "为什么我学会了编程")]
    [InlineData("한글 제목", "한글-제목")] // Hangul must not decompose into jamo
    public void Slugify_OtherScriptsAreKept(string title, string expected) =>
        Assert.Equal(expected, FileNames.Slugify(title, "fallback"));

    [Theory]
    [InlineData("\U0001F923\U0001F923")]
    [InlineData("!!! ???")]
    [InlineData("")]
    [InlineData(null)]
    public void Slugify_FallsBackWhenNothingSurvives(string? title) => Assert.Equal("abc123", FileNames.Slugify(title, "abc123"));

    [Fact]
    public void Slugify_SurvivesALoneSurrogate_WhichUsedToThrow() =>
        Assert.Equal("broken-title", FileNames.Slugify("Broken \ud83d title", "fallback"));

    [Fact]
    public void Slugify_NeverExceedsTheFileNameByteBudget()
    {
        foreach (var letter in new[] { "a", "é", "中", "\U00020000" }) // 1, 2, 3 and 4 bytes in UTF-8
        {
            var slug = FileNames.Slugify(string.Concat(Enumerable.Repeat(letter + " ", 400)), "x");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(slug) <= 150, $"{letter}: {System.Text.Encoding.UTF8.GetByteCount(slug)} bytes");
            Assert.False(slug.EndsWith('-'));
        }
    }

    [Fact]
    public void Slugify_NeverSplitsASurrogatePair()
    {
        var slug = FileNames.Slugify(string.Concat(Enumerable.Repeat("\U00020000", 100)), "x");
        Assert.All(slug.EnumerateRunes(), r => Assert.True(r.Value == 0x20000));
    }

    [Fact]
    public void Slugify_ProducesNothingTheFileSystemObjectsTo()
    {
        var slug = FileNames.Slugify("a/b\\c:d*e?f\"g<h>i|j k.l", "x");
        Assert.Equal("a-b-c-d-e-f-g-h-i-j-k-l", slug);
        Assert.Empty(slug.Intersect(Path.GetInvalidFileNameChars()));
    }
}

public class FrontmatterQuoteTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("line1\nline2", "\"line1\\nline2\"")]
    [InlineData("colon: yes # no", "\"colon: yes # no\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("ctl\u0001", "\"ctl\\x01\"")]
    [InlineData("del\u007f nel\u0085 c1\u009f", "\"del\\x7F nel\\x85 c1\\x9F\"")]
    [InlineData("sep\U00002028\U00002029 bom\U0000FEFF nonchar\U0000FFFE", "\"sep\\u2028\\u2029 bom\\uFEFF nonchar\\uFFFE\"")]
    [InlineData("pair 😀 kept", "\"pair 😀 kept\"")]
    public void Quote(string value, string expected) => Assert.Equal(expected, Frontmatter.Quote(value));

    // not an [InlineData]: xunit cannot carry a lone surrogate through test discovery without replacing it
    [Fact]
    public void Quote_ReplacesLoneSurrogates() =>
        Assert.Equal("\"lone \\uFFFD high and \\uFFFD low\"", Frontmatter.Quote("lone " + (char)0xD83D + " high and " + (char)0xDE00 + " low"));
}
