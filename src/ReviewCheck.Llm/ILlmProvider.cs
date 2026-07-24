namespace ReviewCheck.Llm;

/// <summary>
/// The model seam (docs/25 §2.1): the ONLY door through which ReviewCheck talks to an LLM.
/// One interface, two implementations — BYO key (<see cref="AnthropicByoProvider"/>) now,
/// host sampling later — so the adapter never knows or cares who answers. The contract:
/// system + user prompt in, raw completion text out. Anything that prevents a completion
/// (no key, network down, malformed response) surfaces as <see cref="LlmUnavailableException"/>,
/// which callers treat as "degrade to facts", never as a crash (docs/25 §1.4).
/// </summary>
public interface ILlmProvider
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);
}

/// <summary>
/// The provider cannot produce a completion. Deliberately ONE exception type for every cause
/// (missing key, HTTP error, unreachable API, unexpected shape): the adapter's reaction is the
/// same — retry once, then degrade to facts — so a finer taxonomy would only invite branching.
/// Messages must never contain the API key.
/// </summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
