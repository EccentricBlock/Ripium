using System.Text.Json.Nodes;
using Ripium.Medium;

namespace Ripium.Tests;

public class GraphQlTests
{
    private static JsonNode Resolve(string post, string data) =>
        GraphQlRefResolver.Resolve(JsonNode.Parse(post), (JsonObject)JsonNode.Parse(data)!)!;

    [Fact]
    public void ReplacesSimpleRef()
    {
        var r = Resolve("""{"media":{"__ref":"MediaResource:1"}}""", """{"MediaResource:1":{"id":"1","title":"T"}}""");
        Assert.Equal("T", (string?)r["media"]!["title"]);
    }

    [Fact]
    public void ReplacesNestedAndListRefs()
    {
        var r = Resolve(
            """{"items":[{"__ref":"A:1"},{"__ref":"A:2"}]}""",
            """{"A:1":{"inner":{"__ref":"B:1"}},"A:2":{"n":2},"B:1":{"deep":true}}""");
        Assert.True((bool?)r["items"]![0]!["inner"]!["deep"]);
        Assert.Equal(2, (int?)r["items"]![1]!["n"]);
    }

    [Fact]
    public void FollowsChainedRefs()
    {
        var r = Resolve("""{"x":{"__ref":"A:1"}}""", """{"A:1":{"__ref":"B:1"},"B:1":{"v":"end"}}""");
        Assert.Equal("end", (string?)r["x"]!["v"]);
    }

    [Fact]
    public void LeavesMissingRefInPlace()
    {
        var r = Resolve("""{"x":{"__ref":"Nope:1"}}""", "{}");
        Assert.Equal("Nope:1", (string?)r["x"]!["__ref"]);
    }

    [Fact]
    public void SurvivesCyclicRefs()
    {
        var r = Resolve("""{"x":{"__ref":"A:1"}}""", """{"A:1":{"self":{"__ref":"A:1"}}}""");
        Assert.NotNull(r["x"]);
    }

    [Fact]
    public void ExtractPost_ResolvesRefsAgainstData()
    {
        var post = MediumClient.ExtractPost(
            """{"data":{"post":{"id":"p","m":{"__ref":"M:1"}},"M:1":{"title":"hello"}}}""", "p");
        Assert.Equal("hello", (string?)post["m"]!["title"]);
    }

    [Fact]
    public void ExtractPost_NullPost_IsPostUnavailable() =>
        Assert.Throws<PostUnavailableException>(() => MediumClient.ExtractPost("""{"data":{"post":null}}""", "abc123abc123"));

    [Theory]
    [InlineData("""{"data":null}""", "no data in response")]
    [InlineData("""{"errors":[{"message":"boom"}]}""", "boom")]
    [InlineData("""{"errors":[{"message":42}]}""", "42")]
    public void ExtractPost_NoData_IsAnErrorNotAMissingPost(string body, string expectedMessage)
    {
        // a failed request must stay retryable: it is not an answer about the post
        var ex = Assert.ThrowsAny<MediumException>(() => MediumClient.ExtractPost(body, "abc123abc123"));
        Assert.IsNotType<PostUnavailableException>(ex);
        Assert.Contains(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("<html>Just a moment...</html>")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void ExtractPost_NonObjectOrNonJson_IsAMediumException(string body) =>
        Assert.IsNotType<PostUnavailableException>(Assert.ThrowsAny<MediumException>(() => MediumClient.ExtractPost(body, "x")));

    [Fact]
    public void ExtractPost_ToleratesALoneSurrogateEscape()
    {
        // JavaScript can cut an emoji in half; the article must survive with U+FFFD instead of failing to parse
        var post = MediumClient.ExtractPost("""{"data":{"post":{"title":"cut \ud83d here","ok":"fine \ud83d\ude00"}}}""", "p");
        Assert.Equal("cut \uFFFD here", (string?)post["title"]);
        Assert.Equal("fine \U0001F600", (string?)post["ok"]);
    }

    [Fact]
    public void QueryConstant_IsTheFullPostQuery()
    {
        Assert.StartsWith("query FullPostQuery(", GraphQlQuery.FullPostQuery);
        Assert.Contains("fragment ParagraphData on Paragraph", GraphQlQuery.FullPostQuery);
    }
}
