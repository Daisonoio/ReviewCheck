using System.Text;
using System.Text.Json;

namespace ReviewCheck.Llm;

/// <summary>
/// MVP-3 BYO provider (docs/25 §2.1): the user's own Anthropic key, straight to the Anthropic
/// API over a typed <see cref="HttpClient"/>. No ReviewCheck server in the middle — the block's
/// code goes only to the account the USER configured (GUARDRAILS G7, no phone-home; docs/25 §6).
/// The key comes from the environment (or the ctor for tests) and is NEVER logged: it lives in
/// the request header only, and every exception message is built without it (docs/25 T1 stop-if).
/// </summary>
public sealed class AnthropicByoProvider : ILlmProvider
{
    /// <summary>Primary env var for the key; the standard Anthropic variable works as fallback.</summary>
    public const string KeyVariable = "REVIEWCHECK_ANTHROPIC_KEY";
    public const string FallbackKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>Env var to pick the model; per-block narration is small, the default favors quality.</summary>
    public const string ModelVariable = "REVIEWCHECK_LLM_MODEL";
    public const string DefaultModel = "claude-sonnet-5";

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 1024; // what/why/link JSON — never long-form prose

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    /// <summary>Null <paramref name="apiKey"/>/<paramref name="model"/> = resolve from the environment (production path).</summary>
    public AnthropicByoProvider(HttpClient http, string? apiKey = null, string? model = null)
    {
        _http = http;
        _apiKey = apiKey
                  ?? Environment.GetEnvironmentVariable(KeyVariable)
                  ?? Environment.GetEnvironmentVariable(FallbackKeyVariable);
        _model = model
                 ?? Environment.GetEnvironmentVariable(ModelVariable)
                 ?? DefaultModel;
    }

    /// <summary>True when a key is present in the environment — the DI switch for LLM vs facts-only narration.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FallbackKeyVariable));

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new LlmUnavailableException(
                $"No Anthropic API key configured: set {KeyVariable} (or {FallbackKeyVariable}) " +
                "to enable LLM explanations. Without it, ReviewCheck narrates from structural facts only.");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = _model,
                max_tokens = MaxTokens,
                system,
                messages = new[] { new { role = "user", content = user } },
            }),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Message from the transport, never from us with the key in hand.
            throw new LlmUnavailableException($"Anthropic API unreachable: {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new LlmUnavailableException(
                    $"Anthropic API returned {(int)response.StatusCode}: {Truncate(body)}");

            return ExtractText(body);
        }
    }

    /// <summary>Pulls the first text part out of a Messages API response.</summary>
    private static string ExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var part in doc.RootElement.GetProperty("content").EnumerateArray())
                if (part.TryGetProperty("type", out var type) && type.GetString() == "text")
                    return part.GetProperty("text").GetString() ?? "";

            throw new LlmUnavailableException("Anthropic API response contains no text content.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LlmUnavailableException($"Anthropic API response not in the expected shape: {e.Message}", e);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
