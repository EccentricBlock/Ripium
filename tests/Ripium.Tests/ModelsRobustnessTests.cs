using System.Text.Json.Nodes;
using Ripium.Markdown;
using Ripium.Medium;

namespace Ripium.Tests;

/// <summary>Medium returns nulls and oddities in many places; one bad field must not cost the whole article.</summary>
public class ModelsRobustnessTests
{
    private static Post Parse(string json) => Post.FromJson(JsonNode.Parse(json)!);

    private static string Para(string markupsJson) =>
        """{"content":{"bodyModel":{"paragraphs":[{"type":"P","text":"hello world","markups":""" + markupsJson + "}]}}}";

    [Fact]
    public void NullMarkupOffsets_AreSkipped_NotFatal()
    {
        var post = Parse(Para("""[{"type":"STRONG","start":null,"end":5},{"type":"EM","start":6,"end":11}]"""));
        Assert.Equal("hello *world*", InlineMarkdown.Render(post.Paragraphs[0].Text, post.Paragraphs[0].Markups));
    }

    [Fact]
    public void NullElementsInArrays_AreDropped()
    {
        var post = Parse("""{"tags":[null,{"displayTitle":"Real"}],"content":{"bodyModel":{"paragraphs":[null,{"type":"P","text":"x","markups":[null]}]}}}""");
        Assert.Equal("Real", Assert.Single(post.Tags!).DisplayTitle);
        Assert.Equal("x", Assert.Single(post.Paragraphs).Text);
        Assert.Empty(post.Paragraphs[0].Markups!);
        Assert.Contains("x", MarkdownRenderer.Render(post).Markdown); // and rendering does not trip over them
    }

    [Theory]
    [InlineData("""{"title":"T","firstPublishedAt":99999999999999999,"updatedAt":-99999999999999999}""")]
    [InlineData("""{"title":"T","readingTime":1e308}""")]
    [InlineData("""{"title":"T","readingTime":-5}""")]
    public void OutOfRangeDatesAndDurations_AreLeftOut_NotFatal(string json)
    {
        var markdown = MarkdownRenderer.Render(Parse(json)).Markdown;
        Assert.DoesNotContain("published:", markdown);
        Assert.DoesNotContain("updated:", markdown);
        Assert.DoesNotContain("reading_time:", markdown);
        Assert.Contains("# T", markdown);
    }

    [Fact]
    public void ARealDate_IsStillWritten() =>
        Assert.Contains("published: 2023-02-13T19:48:45Z", MarkdownRenderer.Render(Parse("""{"title":"T","firstPublishedAt":1676317725922}""")).Markdown);

    [Fact]
    public void AMistypedField_BecomesAMediumException_NotARawJsonException()
    {
        var ex = Assert.Throws<MediumException>(() => Parse("""{"title":"T","readingTime":"abc"}"""));
        Assert.Contains("unexpected shape", ex.Message);
    }

    [Fact]
    public void NumbersSentAsStrings_AreAccepted() =>
        Assert.Equal(4.5, Parse("""{"readingTime":"4.5"}""").ReadingTime);

    [Fact]
    public void EmptyObject_IsAnEmptyPost()
    {
        var post = Parse("{}");
        Assert.Empty(post.Paragraphs);
        Assert.Equal("", MarkdownRenderer.Render(post, new RenderOptions(IncludeFrontmatter: false)).Markdown.Trim());
    }

    [Fact]
    public void UnknownFieldsAndTypeNames_AreIgnored() =>
        Assert.Equal("T", Parse("""{"__typename":"Post","title":"T","somethingNew":{"a":[1,2,3]}}""").Title);

    [Fact]
    public void PropertyNamesAreCaseInsensitive() =>
        Assert.Equal("T", Parse("""{"TITLE":"T"}""").Title);
}
