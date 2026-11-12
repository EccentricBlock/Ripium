using Ripium.Medium;

namespace Ripium.Tests;

public class MediumUrlTests
{
    // Vectors ported from Freedium's validators_hashes_test.py
    [Theory]
    [InlineData("stop-wasting-your-life-27832c8f6644", "27832c8f6644")]
    [InlineData("515dd5a43948", "515dd5a43948")]
    [InlineData("https://medium.com/macoclock/12-macos-apps-515dd5a43948", "515dd5a43948")]
    [InlineData("https://www.freedium.cfd/c81e00f6320d", "c81e00f6320d")]
    [InlineData("https://medium.com/dataman-in-ai/predictions-4b439aa3d9bd+", "4b439aa3d9bd")]
    [InlineData("https://medium.com/dataman-in-ai/predictions-4b439aa3d9bd+fds", "4b439aa3d9bd")]
    [InlineData("https://12ft.io/api/proxy?q=https%3A%2F%2Fednico.medium.com%2Fpkm-weekly-2024-05-26-6601694e4147", "6601694e4147")]
    public void ExtractHashes_FindsSinglePostId(string input, string expected) =>
        Assert.Equal([expected], MediumUrl.ExtractHashes(input));

    [Theory]
    [InlineData("")]
    [InlineData("no-hash-here")]
    [InlineData("invalid-hash-123")]
    [InlineData("zzzzzzzzzzz")]
    [InlineData("12345")]
    [InlineData("12345678901234567890")]
    [InlineData("http://freedium.cfd/https://medium.com/funny-ai-quant/guide-2c250f243190T5BbIO-RCOq-9i3=s0-d-e1-ft")]
    public void ExtractHashes_RejectsNonIds(string input) => Assert.Empty(MediumUrl.ExtractHashes(input));

    [Fact]
    public void ExtractHashes_NullIsEmpty() => Assert.Empty(MediumUrl.ExtractHashes(null));

    [Fact]
    public void ExtractHashes_ReturnsAllMatches() =>
        Assert.Equal(["515dd5a43948", "c81e00f6320d"], MediumUrl.ExtractHashes("first-515dd5a43948 second-c81e00f6320d"));

    [Theory]
    [InlineData("https://medium.com/@godfreythegreat/stop-wasting-your-life-27832c8f6644", "27832c8f6644")]
    [InlineData("https://medium.com/@godfreythegreat/stop-wasting-your-life-27832c8f6644/", "27832c8f6644")]
    [InlineData("medium.com/p/27832c8f6644", "27832c8f6644")]
    [InlineData("https://towardsdatascience.com/some-title-here-9dc802a10618", "9dc802a10618")]
    [InlineData("https://valeman.medium.com/python-vs-r-for-time-series-forecasting-395390432598", "395390432598")] // digits only
    [InlineData("27832c8f6644", "27832c8f6644")]
    [InlineData("https://l.facebook.com/l.php?u=https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://webcache.googleusercontent.com/search?q=cache:https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://www.google.com/url?url=https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://www.google.com/url?q=https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://12ft.io/proxy?q=https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://medium.com/m/global-identity-2?redirectUrl=https://medium.com/story-77ae792a1a43", "77ae792a1a43")]
    [InlineData("https://freedium.example/https://medium.com/@a/my-story-77ae792a1a43", "77ae792a1a43")]
    // a hash-like tracking value in the query must not win over the id in the path
    [InlineData("https://medium.com/@a/codeium-best-5a010f74d3e1?subid1=20241023-0155-4648-992f-1316e4a9d771", "5a010f74d3e1")]
    public async Task ResolvePostId_HandlesUrlShapes(string input, string expected) =>
        Assert.Equal(expected, await MediumUrl.ResolvePostIdAsync(input));

    [Fact]
    public async Task ResolvePostId_FollowsShortLinks()
    {
        string? requested = null;
        var id = await MediumUrl.ResolvePostIdAsync("https://link.medium.com/vYe3nWA8wBb", (shortId, _) =>
        {
            requested = shortId;
            return Task.FromResult<string?>("https://medium.com/story-77ae792a1a43");
        });
        Assert.Equal("vYe3nWA8wBb", requested);
        Assert.Equal("77ae792a1a43", id);
    }

    // Freedium falls back to "the last id-shaped token anywhere in the input"; so do we, minus the query string.
    [Theory]
    [InlineData("stop-wasting-your-life-27832c8f6644", "27832c8f6644")]
    [InlineData("https://medium.com/@a/title-27832c8f6644/amp", "27832c8f6644")]
    [InlineData("27832c8f6644?source=newsletter", "27832c8f6644")]
    [InlineData("medium.com/@a/title-27832c8f6644#responses", "27832c8f6644")]
    [InlineData("  https://medium.com/@a/title-27832c8f6644  ", "27832c8f6644")]
    public async Task ResolvePostId_FallsBackToTheLastIdShapedToken(string input, string expected) =>
        Assert.Equal(expected, await MediumUrl.ResolvePostIdAsync(input));

    [Fact]
    public async Task ResolvePostId_FallbackIgnoresHashLikeValuesInTheQuery() =>
        await Assert.ThrowsAsync<InvalidMediumUrlException>(() =>
            MediumUrl.ResolvePostIdAsync("https://example.com/about?subid1=20241023-0155-4648-992f-1316e4a9d771"));

    [Theory]
    [InlineData("https://medium.com/invalid-story")]
    [InlineData("https://medium.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("https://l.facebook.com/l.php")]
    [InlineData("https://link.medium.com/abc")] // no resolver available
    public async Task ResolvePostId_ThrowsForNonMediumInput(string input) =>
        await Assert.ThrowsAsync<InvalidMediumUrlException>(() => MediumUrl.ResolvePostIdAsync(input));

    [Fact]
    public async Task ResolvePostId_GivesUpOnRedirectLoops() =>
        await Assert.ThrowsAsync<InvalidMediumUrlException>(() => MediumUrl.ResolvePostIdAsync(
            "https://link.medium.com/loop", (_, _) => Task.FromResult<string?>("https://link.medium.com/loop")));
}
