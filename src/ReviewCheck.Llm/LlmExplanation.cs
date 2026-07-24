namespace ReviewCheck.Llm;

/// <summary>
/// What the LLM is allowed to produce — and NOTHING else (docs/25 §0): the three narrative
/// fields plus its own declared uncertainty. Code and citations are absent by design; they
/// are stapled onto the <c>Block</c> from the pipeline, so the LLM cannot invent evidence.
/// </summary>
public sealed record LlmExplanation(
    string What,
    string Why,
    string Link,
    string? UncertaintySemantic);
