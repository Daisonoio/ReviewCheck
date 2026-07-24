using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Scripted <see cref="ILlmProvider"/> (docs/25 T2): the LLM is not deterministic, so the
/// adapter's MECHANICS (parse, validation, retry, degradation) are tested against outputs
/// scripted here — valid, invalid, verdict-laden, hallucinated — with full determinism.
/// Records every call so tests can assert on the prompts too.
/// </summary>
public sealed class FakeLlmProvider : ILlmProvider
{
    private readonly Queue<Func<string>> _script = new();

    /// <summary>Every (system, user) prompt pair received, in order.</summary>
    public List<(string System, string User)> Calls { get; } = [];

    /// <summary>Script the next completion to return this text.</summary>
    public FakeLlmProvider Returns(string output)
    {
        _script.Enqueue(() => output);
        return this;
    }

    /// <summary>Script the next completion to fail as an unavailable LLM.</summary>
    public FakeLlmProvider Fails(string message = "LLM down (scripted)")
    {
        _script.Enqueue(() => throw new LlmUnavailableException(message));
        return this;
    }

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        Calls.Add((system, user));
        if (_script.Count == 0)
            throw new InvalidOperationException(
                "FakeLlmProvider: no scripted output left — script one with Returns()/Fails().");
        return Task.FromResult(_script.Dequeue()());
    }
}
