using ReviewCheck.Llm;

namespace ReviewCheck.Evals;

/// <summary>
/// A scripted <see cref="ILlmProvider"/> for the LLM-behaviour evals. The model is not
/// deterministic, so to evaluate the GUARDRAIL NET (does bad model output get caught?) we script
/// the model's replies — valid, verdict-laden, hallucinated, or unavailable — and assert on what
/// the adapter lets through. Same idea as the unit-test fake, kept here so the eval harness is
/// self-contained and doesn't depend on the test projects.
/// </summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly Queue<Func<string>> _script = new();

    public ScriptedLlmProvider Returns(string output)
    {
        _script.Enqueue(() => output);
        return this;
    }

    public ScriptedLlmProvider Fails(string message = "LLM unavailable (scripted)")
    {
        _script.Enqueue(() => throw new LlmUnavailableException(message));
        return this;
    }

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        if (_script.Count == 0)
            throw new InvalidOperationException("ScriptedLlmProvider: no scripted reply left.");
        return Task.FromResult(_script.Dequeue()());
    }

    /// <summary>A well-formed reply in the model's JSON contract (what/why/link/uncertainty_semantic).</summary>
    public static string Json(string what, string why, string link, string? uncertainty = null) =>
        $$"""
        { "what": {{Q(what)}}, "why": {{Q(why)}}, "link": {{Q(link)}}, "uncertainty_semantic": {{(uncertainty is null ? "null" : Q(uncertainty))}} }
        """;

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
