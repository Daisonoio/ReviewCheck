using Microsoft.Extensions.AI;
using ReviewCheck.Llm;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// LLM narration delegated to the HOST model via MCP sampling (docs/25 §2.1, the
/// <c>HostSamplingProvider</c>): no dedicated API key — ReviewCheck asks the host (Claude Code)
/// to run the completion on the model the user already uses, then applies the SAME rubric +
/// citation stapling as the BYO path. So the narrative stays grounded; only the token source
/// differs. Only used when the host declared the <c>sampling</c> capability.
/// </summary>
public sealed class HostSamplingLlmProvider(IChatClient chat) : ILlmProvider
{
    private const int MaxTokens = 1024; // what/why/link JSON — never long-form prose

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system),
                new(ChatRole.User, user),
            };
            var response = await chat.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = MaxTokens }, ct);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
                throw new LlmUnavailableException("host sampling returned an empty result.");
            return text;
        }
        catch (Exception e) when (e is not LlmUnavailableException)
        {
            // The host refused, timed out, or the transport failed — treat like any unavailable LLM.
            throw new LlmUnavailableException($"host sampling failed: {e.Message}", e);
        }
    }
}
