using System.Net;
using System.Text.Json;
using ReviewCheck.Platform;

namespace ReviewCheck.Platform.Tests;

/// <summary>
/// Offline against a stub <see cref="HttpMessageHandler"/> — no real GitHub call in this suite.
/// Covers: token-missing failure names the variable (mirrors <c>AnthropicByoProviderTests</c>'
/// no-key gate), the self-review flag in <c>ListAsync</c> (GUARDRAILS G10's data half), the diff
/// endpoint's Accept header, the single-call shape of <c>SubmitReviewAsync</c> (GUARDRAILS G8/G9:
/// there is nothing else on the interface that could post early or post LLM text), and that the
/// token never leaks into an exception message.
/// </summary>
public sealed class GitHubPullRequestPlatformTests
{
    private const string Token = "ghp_test-SECRET-do-not-leak";

    /// <summary>Plays the GitHub API: canned status + body, and records the request.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    /// <summary>Plays a sequence of responses — needed because ListAsync makes two calls (whoami, then pulls).</summary>
    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _index;
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused (scripted)");
    }

    // ---- no token: a clear error, naming the variable ----

    [Fact]
    public async Task NoToken_Throws_NamingTheVariableToSet()
    {
        var platform = new GitHubPullRequestPlatform(
            new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")), token: "");

        var ex = await Assert.ThrowsAsync<PullRequestPlatformUnavailableException>(
            () => platform.GetAuthenticatedLoginAsync());

        Assert.Contains(GitHubPullRequestPlatform.TokenVariable, ex.Message);
    }

    // ---- GetAuthenticatedLoginAsync ----

    [Fact]
    public async Task GetAuthenticatedLogin_ReturnsLogin_AndSendsBearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{ "login": "alice" }""");
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var login = await platform.GetAuthenticatedLoginAsync();

        Assert.Equal("alice", login);
        Assert.Equal($"Bearer {Token}", handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    // ---- ListAsync: the self-review flag (GUARDRAILS G10 data half) ----

    [Fact]
    public async Task ListAsync_FlagsThePrAuthoredByTheAuthenticatedUser_AsSelfReview()
    {
        var pulls = JsonSerializer.Serialize(new[]
        {
            new { number = 1, title = "Not mine", user = new { login = "bob" } },
            new { number = 2, title = "Mine", user = new { login = "alice" } },
        });
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{ "login": "alice" }"""),
            (HttpStatusCode.OK, pulls));
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var result = await platform.ListAsync("owner/repo");

        Assert.Equal(2, result.Count);
        Assert.False(result.Single(p => p.Number == "1").IsSelfReview);
        Assert.True(result.Single(p => p.Number == "2").IsSelfReview);
    }

    // ---- GetSummaryAsync: same self-review flag, for a PR opened directly by number (GUARDRAILS G10) ----

    [Fact]
    public async Task GetSummaryAsync_SelfAuthored_FlagsIsSelfReviewTrue()
    {
        var pr = JsonSerializer.Serialize(new { number = 2, title = "Mine", user = new { login = "alice" } });
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{ "login": "alice" }"""),
            (HttpStatusCode.OK, pr));
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var summary = await platform.GetSummaryAsync("owner/repo", "2");

        Assert.Equal("2", summary.Number);
        Assert.Equal("alice", summary.Author);
        Assert.True(summary.IsSelfReview);
    }

    [Fact]
    public async Task GetSummaryAsync_AuthoredByOthers_FlagsIsSelfReviewFalse()
    {
        var pr = JsonSerializer.Serialize(new { number = 7, title = "Not mine", user = new { login = "bob" } });
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{ "login": "alice" }"""),
            (HttpStatusCode.OK, pr));
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var summary = await platform.GetSummaryAsync("owner/repo", "7");

        Assert.False(summary.IsSelfReview);
    }

    // ---- GetDiffAsync: asks for the diff media type, not JSON ----

    [Fact]
    public async Task GetDiffAsync_RequestsTheDiffAcceptHeader_AndReturnsBodyVerbatim()
    {
        const string diffText = "diff --git a/x.cs b/x.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n";
        var handler = new StubHandler(HttpStatusCode.OK, diffText);
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var diff = await platform.GetDiffAsync("owner/repo", "42");

        Assert.Equal(diffText, diff);
        Assert.Equal("application/vnd.github.v3.diff", handler.LastRequest!.Headers.Accept.ToString());
        Assert.Contains("/repos/owner/repo/pulls/42", handler.LastRequest.RequestUri!.ToString());
    }

    // ---- SubmitReviewAsync: the ONLY posting call — carries the event and exactly the given comments ----

    [Fact]
    public async Task SubmitReviewAsync_Approve_SendsApproveEvent_NoComments()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        await platform.SubmitReviewAsync("owner/repo", "42", PullRequestReviewEvent.Approve, []);

        Assert.Contains("\"event\":\"APPROVE\"", handler.LastRequestBody);
        Assert.Contains("/repos/owner/repo/pulls/42/reviews", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SubmitReviewAsync_RequestChanges_CarriesTheReviewersOwnNoteVerbatim()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);
        var comments = new[] { new PullRequestComment("src/Foo.cs", "12-14", "please add a null check here") };

        await platform.SubmitReviewAsync("owner/repo", "42", PullRequestReviewEvent.RequestChanges, comments);

        Assert.Contains("\"event\":\"REQUEST_CHANGES\"", handler.LastRequestBody);
        Assert.Contains("please add a null check here", handler.LastRequestBody);
        Assert.Contains("\"path\":\"src/Foo.cs\"", handler.LastRequestBody);
        Assert.Contains("\"line\":14", handler.LastRequestBody); // the range's last line
    }

    [Fact]
    public async Task SubmitReviewAsync_CommentOnly_SendsCommentEvent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        await platform.SubmitReviewAsync("owner/repo", "42", PullRequestReviewEvent.Comment, []);

        Assert.Contains("\"event\":\"COMMENT\"", handler.LastRequestBody);
    }

    // ---- error handling: never leak the token, distinguish rejection from unreachable ----

    [Fact]
    public async Task ApiError_Throws_WithoutLeakingTheToken()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{ "message": "Bad credentials" }""");
        var platform = new GitHubPullRequestPlatform(new HttpClient(handler), Token);

        var ex = await Assert.ThrowsAsync<PullRequestPlatformUnavailableException>(
            () => platform.GetAuthenticatedLoginAsync());

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_Throws_PlatformUnavailable_WithoutLeakingTheToken()
    {
        var platform = new GitHubPullRequestPlatform(new HttpClient(new ThrowingHandler()), Token);

        var ex = await Assert.ThrowsAsync<PullRequestPlatformUnavailableException>(
            () => platform.GetAuthenticatedLoginAsync());

        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public void IsConfigured_FalseWithoutEnvVar()
    {
        Environment.SetEnvironmentVariable(GitHubPullRequestPlatform.TokenVariable, null);
        Assert.False(GitHubPullRequestPlatform.IsConfigured);
    }
}
