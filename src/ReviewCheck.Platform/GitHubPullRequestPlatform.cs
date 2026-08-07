using System.Text;
using System.Text.Json;

namespace ReviewCheck.Platform;

/// <summary>
/// GitHub REST implementation of <see cref="IPullRequestPlatform"/>. The user's own token, straight to
/// the GitHub API over a typed <see cref="HttpClient"/> — no ReviewCheck server in the middle, same
/// shape as <c>ReviewCheck.Llm.AnthropicByoProvider</c> (GUARDRAILS G7: the only network egress is the
/// platform, with the user's own credential). The token is never logged: it lives in the request
/// header only, and every exception message is built without it.
/// </summary>
public sealed class GitHubPullRequestPlatform : IPullRequestPlatform
{
    /// <summary>Env var for the token; a fine-grained PAT scoped to "Pull requests: Read and write" on the target repo(s) is the least-privilege choice.</summary>
    public const string TokenVariable = "GITHUB_TOKEN";

    private const string ApiBase = "https://api.github.com";
    private const string ApiVersion = "2022-11-28";
    private const string DiffAcceptHeader = "application/vnd.github.v3.diff";
    private const string JsonAcceptHeader = "application/vnd.github+json";
    private const string RawAcceptHeader = "application/vnd.github.raw";

    private readonly HttpClient _http;
    private readonly string? _token;

    /// <summary>Null <paramref name="token"/> = resolve from the environment (production path).</summary>
    public GitHubPullRequestPlatform(HttpClient http, string? token = null)
    {
        _http = http;
        _token = token ?? Environment.GetEnvironmentVariable(TokenVariable);
    }

    /// <summary>True when a token is present in the environment — the DI switch for PR review support.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenVariable));

    public async Task<IReadOnlyList<PullRequestSummary>> ListAsync(string repo, CancellationToken ct = default)
    {
        var login = await GetAuthenticatedLoginAsync(ct);
        var body = await SendAsync(HttpMethod.Get, $"/repos/{repo}/pulls?state=open", null, JsonAcceptHeader, ct);

        using var doc = JsonDocument.Parse(body);
        var result = new List<PullRequestSummary>();
        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            var author = pr.GetProperty("user").GetProperty("login").GetString() ?? "";
            result.Add(new PullRequestSummary(
                Number: pr.GetProperty("number").GetInt32().ToString(),
                Title: pr.GetProperty("title").GetString() ?? "",
                Author: author,
                IsSelfReview: string.Equals(author, login, StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    public async Task<PullRequestSummary> GetSummaryAsync(string repo, string pr, CancellationToken ct = default)
    {
        var login = await GetAuthenticatedLoginAsync(ct);
        var body = await SendAsync(HttpMethod.Get, $"/repos/{repo}/pulls/{pr}", null, JsonAcceptHeader, ct);

        using var doc = JsonDocument.Parse(body);
        var author = doc.RootElement.GetProperty("user").GetProperty("login").GetString() ?? "";
        return new PullRequestSummary(
            Number: doc.RootElement.GetProperty("number").GetInt32().ToString(),
            Title: doc.RootElement.GetProperty("title").GetString() ?? "",
            Author: author,
            IsSelfReview: string.Equals(author, login, StringComparison.OrdinalIgnoreCase));
    }

    public Task<string> GetDiffAsync(string repo, string pr, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"/repos/{repo}/pulls/{pr}", null, DiffAcceptHeader, ct);

    public async Task<string> GetHeadRefAsync(string repo, string pr, CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, $"/repos/{repo}/pulls/{pr}", null, JsonAcceptHeader, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("head", out var head) &&
               head.TryGetProperty("sha", out var sha) && sha.GetString() is { } s
            ? s
            : throw new PullRequestPlatformUnavailableException("GitHub PR response has no head.sha.");
    }

    /// <summary>
    /// Any failure (missing file, network blip, whatever) degrades to null rather than throwing —
    /// mirrors <c>LocalDiffReader.LoadNewText</c>'s broad catch: one unreadable file degrades that
    /// block gracefully (P8), it never fails the whole review.
    /// </summary>
    public async Task<string?> GetFileContentAsync(string repo, string @ref, string path, CancellationToken ct = default)
    {
        try
        {
            var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            return await SendAsync(
                HttpMethod.Get, $"/repos/{repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(@ref)}",
                null, RawAcceptHeader, ct);
        }
        catch (PullRequestPlatformUnavailableException)
        {
            return null;
        }
    }

    public async Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, "/user", null, JsonAcceptHeader, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("login", out var login) && login.GetString() is { } s
            ? s
            : throw new PullRequestPlatformUnavailableException("GitHub /user response has no login.");
    }

    public Task SubmitReviewAsync(
        string repo, string pr, PullRequestReviewEvent reviewEvent,
        IReadOnlyList<PullRequestComment> comments, string? body = null, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = ToGitHubEvent(reviewEvent),
            body = body ?? "",
            comments = comments.Select(c => new { path = c.Path, line = ParseLine(c.Line), body = c.Body }),
        });
        return SendAsync(HttpMethod.Post, $"/repos/{repo}/pulls/{pr}/reviews", payload, JsonAcceptHeader, ct);
    }

    private static string ToGitHubEvent(PullRequestReviewEvent reviewEvent) => reviewEvent switch
    {
        PullRequestReviewEvent.Approve => "APPROVE",
        PullRequestReviewEvent.RequestChanges => "REQUEST_CHANGES",
        PullRequestReviewEvent.Comment => "COMMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(reviewEvent), reviewEvent, null),
    };

    /// <summary>A citation's <c>Lines</c> is a range like "12-14" or a single "12"; GitHub wants the last line.</summary>
    private static int ParseLine(string line)
    {
        var last = line.Split('-')[^1];
        return int.TryParse(last, out var n)
            ? n
            : throw new PullRequestPlatformUnavailableException($"Invalid line reference for a review comment: '{line}'.");
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, string? jsonBody, string acceptHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new PullRequestPlatformUnavailableException(
                $"No GitHub token configured: set {TokenVariable} to enable pull request review.");

        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Add("Authorization", $"Bearer {_token}");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.Add("User-Agent", "ReviewCheck");
        request.Headers.Add("Accept", acceptHeader);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Message from the transport, never from us with the token in hand.
            throw new PullRequestPlatformUnavailableException($"GitHub API unreachable: {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new PullRequestPlatformUnavailableException(
                    $"GitHub API returned {(int)response.StatusCode}: {Truncate(body)}");

            return body;
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
