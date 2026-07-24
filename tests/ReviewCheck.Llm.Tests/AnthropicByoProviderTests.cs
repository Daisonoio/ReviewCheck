using System.Net;
using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 T1 gates: with a key the provider returns the completion; without a key it fails with
/// a CLEAR error naming the variable to set; and the key never leaks into an exception message
/// (the stop-if). All offline — the Anthropic API is played by a stub HttpMessageHandler.
/// </summary>
public sealed class AnthropicByoProviderTests
{
    private const string Key = "sk-ant-test-SECRET-do-not-leak";

    /// <summary>Plays the Anthropic API: canned status + body, and records the request.</summary>
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused (scripted)");
    }

    private static string OkBody(string text) =>
        $$"""{ "content": [ { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} } ] }""";

    // ---- T1 gate: without a key, a clear error ----

    [Fact]
    public async Task NoKey_Throws_NamingTheVariableToSet()
    {
        // Explicit empty key: no env fallback, deterministic regardless of the machine.
        var provider = new AnthropicByoProvider(new HttpClient(new StubHandler(HttpStatusCode.OK, OkBody("x"))), apiKey: "");

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => provider.CompleteAsync("s", "u"));

        Assert.Contains(AnthropicByoProvider.KeyVariable, ex.Message);
        Assert.Contains(AnthropicByoProvider.FallbackKeyVariable, ex.Message);
    }

    // ---- T1 gate: with a key, returns the completion ----

    [Fact]
    public async Task ValidResponse_ReturnsTheCompletionText()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody("the explanation"));
        var provider = new AnthropicByoProvider(new HttpClient(handler), Key);

        var text = await provider.CompleteAsync("system rules", "user facts");

        Assert.Equal("the explanation", text);
    }

    [Fact]
    public async Task Request_CarriesKeyHeader_ModelAndBothPrompts()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody("ok"));
        var provider = new AnthropicByoProvider(new HttpClient(handler), Key, model: "claude-test-model");

        await provider.CompleteAsync("SYSTEM-RULES", "USER-FACTS");

        Assert.Equal([Key], handler.LastRequest!.Headers.GetValues("x-api-key"));
        Assert.Contains("claude-test-model", handler.LastRequestBody);
        Assert.Contains("SYSTEM-RULES", handler.LastRequestBody);
        Assert.Contains("USER-FACTS", handler.LastRequestBody);
    }

    // ---- T1 stop-if: the key must never leak into error messages ----

    [Fact]
    public async Task ApiError_Throws_WithoutLeakingTheKey()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{ "error": "invalid api key" }""");
        var provider = new AnthropicByoProvider(new HttpClient(handler), Key);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => provider.CompleteAsync("s", "u"));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_Throws_LlmUnavailable_WithoutLeakingTheKey()
    {
        var provider = new AnthropicByoProvider(new HttpClient(new ThrowingHandler()), Key);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => provider.CompleteAsync("s", "u"));

        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task MalformedResponse_Throws_LlmUnavailable_NotACrash()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not json at all");
        var provider = new AnthropicByoProvider(new HttpClient(handler), Key);

        await Assert.ThrowsAsync<LlmUnavailableException>(() => provider.CompleteAsync("s", "u"));
    }
}
