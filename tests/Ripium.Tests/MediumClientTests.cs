using System.Net;
using System.Text;
using Ripium.Medium;

namespace Ripium.Tests;

public class MediumClientTests
{
    private const string PostBody = """{"data":{"post":{"id":"abc123abc123","title":"T","m":{"__ref":"M:1"}},"M:1":{"v":"resolved"}}}""";

    /// <summary>Scripted transport: answers each call with the next response factory and records what was sent.</summary>
    private sealed class FakeTransport(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script) : HttpMessageHandler
    {
        public int Calls;
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await script[Math.Min(n, script.Length) - 1](request, ct);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body = "") =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(Exception ex) =>
        (_, _) => throw ex;

    private static MediumClient Client(FakeTransport transport, int attempts = 2, string? cookies = null, TimeSpan? timeout = null) =>
        new(cookies, attempts, transport, timeout, retryDelay: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task SendsTheAppRequest_AndResolvesRefs()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport, cookies: "uid=1; sid=2");

        var post = await client.GetPostAsync("abc123abc123");

        Assert.Equal("resolved", (string?)post["m"]!["v"]);
        var (request, body) = Assert.Single(transport.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://medium.com/_/graphql", request.RequestUri!.ToString());
        Assert.Equal("android", request.Headers.GetValues("X-Obvious-CID").Single());
        Assert.Equal("FullPostQuery", request.Headers.GetValues("X-APOLLO-OPERATION-NAME").Single());
        Assert.Contains("multipart/mixed", string.Join(", ", request.Headers.GetValues("Accept")));
        Assert.Equal("uid=1; sid=2", request.Headers.GetValues("Cookie").Single());
        // .NET splits the header into product tokens; the unterminated tail is Freedium's and is deliberate
        Assert.EndsWith("YandexMobileBot/3.0;", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Contains("\"operationName\":\"FullPostQuery\"", body);
        Assert.Contains("\"postId\":\"abc123abc123\"", body);
    }

    [Fact]
    public async Task NoCookieHeader_WhenNoneConfigured()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport, cookies: "  ");
        await client.GetPostAsync("abc123abc123");
        Assert.False(transport.Sent[0].Request.Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TransientStatuses_AreRetried(HttpStatusCode transient)
    {
        var transport = new FakeTransport(Respond(transient, "slow down"), Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport);
        await client.GetPostAsync("abc123abc123");
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task PermanentClientErrors_AreNotRetried(HttpStatusCode permanent)
    {
        var transport = new FakeTransport(Respond(permanent, "nope"), Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport);

        var ex = await Assert.ThrowsAsync<MediumHttpException>(() => client.GetPostAsync("abc123abc123"));

        Assert.Equal(permanent, ex.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Forbidden_MentionsCloudflare() =>
        Assert.Contains("blocking", (await Assert.ThrowsAsync<MediumHttpException>(
            () => Client(new FakeTransport(Respond(HttpStatusCode.Forbidden))).GetPostAsync("abc123abc123"))).Message);

    [Fact]
    public async Task MissingPost_IsAnAnswer_NotRetried()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.OK, """{"data":{"post":null}}"""), Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport);

        await Assert.ThrowsAsync<PostUnavailableException>(() => client.GetPostAsync("abc123abc123"));

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task RetriesUpToTheConfiguredAttempts_ThenReportsTheLastReason()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.ServiceUnavailable, "down"));
        using var client = Client(transport, attempts: 3);

        var ex = await Assert.ThrowsAsync<MediumException>(() => client.GetPostAsync("abc123abc123"));

        Assert.Equal(3, transport.Calls);
        Assert.Contains("Failed to fetch post abc123abc123", ex.Message);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task NetworkErrors_AreRetried_AndWrapped()
    {
        var transport = new FakeTransport(Throw(new HttpRequestException("connection reset")), Respond(HttpStatusCode.OK, PostBody));
        using var client = Client(transport);
        await client.GetPostAsync("abc123abc123");
        Assert.Equal(2, transport.Calls);

        var failing = new FakeTransport(Throw(new HttpRequestException("connection reset")));
        var ex = await Assert.ThrowsAsync<MediumException>(() => Client(failing).GetPostAsync("abc123abc123"));
        Assert.Contains("connection reset", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ABlockPageWithStatus200_IsRetried_ThenReportedAsNotJson()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.OK, "<html>Just a moment...</html>"));
        using var client = Client(transport);

        var ex = await Assert.ThrowsAsync<MediumException>(() => client.GetPostAsync("abc123abc123"));

        Assert.Equal(2, transport.Calls);
        Assert.Contains("other than JSON", ex.Message);
    }

    [Fact]
    public async Task ATimeout_IsReportedAsATimeout()
    {
        var transport = new FakeTransport(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var client = Client(transport, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<MediumException>(() => client.GetPostAsync("abc123abc123"));

        Assert.Contains("timed out", ex.Message);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task CallerCancellation_IsNotWrappedAndNotRetried()
    {
        var transport = new FakeTransport(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var client = Client(transport, attempts: 3);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPostAsync("abc123abc123", cts.Token));

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task OneAttempt_MeansNoRetry()
    {
        var transport = new FakeTransport(Respond(HttpStatusCode.InternalServerError));
        using var client = Client(transport, attempts: 1);
        await Assert.ThrowsAsync<MediumException>(() => client.GetPostAsync("abc123abc123"));
        Assert.Equal(1, transport.Calls);
    }
}
