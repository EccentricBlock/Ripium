using System.Text.Json.Nodes;
using Ripium.Markdown;
using Ripium.Medium;
using YamlDotNet.Serialization;

namespace Ripium.Tests;

/// <summary>The frontmatter is machine-read by Obsidian, Hugo, Jekyll and friends, so it is checked with a real YAML parser.</summary>
public class FrontmatterYamlTests
{
    private static Dictionary<object, object> ParseFrontmatter(string markdown)
    {
        Assert.StartsWith("---\n", markdown);
        var end = markdown.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        Assert.True(end > 0, "frontmatter is not closed");
        return new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(markdown[4..end]);
    }

    public static TheoryData<string> TrickyStrings() => new()
    {
        "plain",
        "colon: here # and a hash",
        "- looks like a list item",
        "[looks, like, flow]",
        "{looks: like, a: map}",
        "yes", "no", "null", "~", "true", "123", "1e3", "0x1F", "2024-01-01", "",
        "quotes \" and ' and \\ backslash",
        "multi\nline\r\nwith\ttab",
        "emoji \U0001F600\U0001F680 and caf\U000000E9 and \U00004E2D\U00006587",
        "ctl \u0001\u0002\u001f del \u007f nel \u0085 c1 \u0090",
        "sep \U00002028 \U00002029 bom \U0000FEFF nonchars \U0000FFFE \U0000FFFF",
        "!tag &anchor *alias %percent @at `tick",
    };

    [Theory]
    [MemberData(nameof(TrickyStrings))]
    public void QuotedScalar_RoundTripsThroughARealYamlParser(string value) =>
        Assert.Equal(value, (string)ParseFrontmatter("---\nkey: " + Frontmatter.Quote(value) + "\n---\n")["key"]);

    [Fact]
    public void AVeryLongScalar_RoundTrips()
    {
        var value = string.Concat(Enumerable.Repeat("word: # \"quoted\" ", 2000));
        Assert.Equal(value, (string)ParseFrontmatter("---\nkey: " + Frontmatter.Quote(value) + "\n---\n")["key"]);
    }

    [Fact]
    public void LoneSurrogate_BecomesTheReplacementCharacter()
    {
        var parsed = ParseFrontmatter("---\nkey: " + Frontmatter.Quote("a \ud83d b \ude00 c 😀") + "\n---\n");
        Assert.Equal("a \U0000FFFD b \U0000FFFD c \U0001F600", (string)parsed["key"]);
    }

    [Fact]
    public void ARealisticPost_ProducesValidFrontmatterWithTheExpectedKeys()
    {
        var post = Post.FromJson(JsonNode.Parse("""
            {"title":"Why \"quotes\": and #hashes\\ break things","previewContent":{"subtitle":"- a subtitle: with [syntax]"},
             "creator":{"name":"Ada: The \"First\"","username":"ada"},"collection":{"name":"Pub # 1"},
             "firstPublishedAt":1676317725922,"updatedAt":1706944392000,"readingTime":4.2,
             "canonicalUrl":"https://medium.com/@ada/x-abc123abc123","previewImage":{"id":"1*p.png"},
             "tags":[{"displayTitle":"C++"},{"displayTitle":"Yes"},{"displayTitle":"2024"}],
             "content":{"bodyModel":{"paragraphs":[{"type":"P","text":"body"}]}}}
            """)!);

        var yaml = ParseFrontmatter(MarkdownRenderer.Render(post).Markdown);

        Assert.Equal("Why \"quotes\": and #hashes\\ break things", (string)yaml["title"]);
        Assert.Equal("- a subtitle: with [syntax]", (string)yaml["subtitle"]);
        Assert.Equal("Ada: The \"First\"", (string)yaml["author"]);
        Assert.Equal("https://medium.com/@ada", (string)yaml["author_url"]);
        Assert.Equal("Pub # 1", (string)yaml["publication"]);
        Assert.Equal("2023-02-13T19:48:45Z", (string)yaml["published"]);
        Assert.Equal("2024-02-03T07:13:12Z", (string)yaml["updated"]);
        Assert.Equal("5", (string)yaml["reading_time"]);
        Assert.Equal("https://medium.com/@ada/x-abc123abc123", (string)yaml["url"]);
        Assert.Equal(["C++", "Yes", "2024"], ((List<object>)yaml["tags"]).Cast<string>().ToArray());
        Assert.StartsWith("https://miro.medium.com/", (string)yaml["preview_image"]);
    }

    public static TheoryData<string> Fixtures() =>
        new() { "27832c8f6644", "252990763468", "4e4017b89905", "9dc802a10618", "d4222bd1a0b3" };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixture_HasParseableFrontmatterMatchingThePost(string id)
    {
        var post = Post.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"post_{id}.json")))!);
        var result = MarkdownRenderer.Render(post);
        var yaml = ParseFrontmatter(result.Markdown);

        Assert.Equal(result.Title, (string)yaml["title"]);
        Assert.Equal(post.Creator!.Name, (string)yaml["author"]);
        Assert.Equal(post.Tags!.Select(t => t.DisplayTitle), ((List<object>)yaml["tags"]).Cast<string>());
    }
}
