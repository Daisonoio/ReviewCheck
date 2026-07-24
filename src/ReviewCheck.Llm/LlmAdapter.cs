using ReviewCheck.Core;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm;

/// <summary>
/// The ONLY point where the AI enters ReviewCheck (docs/25) — and it is reined in. Per block:
/// prompt → <see cref="ILlmProvider.CompleteAsync"/> → parse → rubric → assemble. Invalid
/// output is retried ONCE with the violation quoted back; a second failure — or an unavailable
/// LLM — degrades to the deterministic facts narrative. A valid <see cref="Block"/> ALWAYS
/// comes out (§1.4), and <c>Code</c> + <c>Citations</c> are stapled verbatim from the pipeline:
/// the LLM narrates, it never anchors (§1.1). One block per call: independent explanations,
/// minimal data surface (§6).
/// </summary>
public sealed class LlmAdapter(ILlmProvider provider) : IBlockNarrator
{
    public async Task<IReadOnlyList<Block>> ExplainAsync(
        IReadOnlyList<StructuralBlock> blocks, CancellationToken ct = default)
    {
        var result = new List<Block>(blocks.Count);
        foreach (var block in blocks)
            result.Add(await ExplainOneAsync(block, ct));
        return result;
    }

    public Task<IReadOnlyList<Block>> NarrateAsync(IReadOnlyList<StructuralBlock> blocks, CancellationToken ct = default) =>
        ExplainAsync(blocks, ct);

    private async Task<Block> ExplainOneAsync(StructuralBlock block, CancellationToken ct)
    {
        var user = PromptBuilder.User(block);
        string? violation = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            string raw;
            try
            {
                raw = await provider.CompleteAsync(PromptBuilder.System(violation), user, ct);
            }
            catch (LlmUnavailableException e)
            {
                // LLM down: no retry ping-pong against a dead endpoint — straight to facts (§3 step 4).
                return Degrade(block, e.Message);
            }

            if (!LlmOutputParser.TryParse(raw, out var parsed, out var parseError))
            {
                violation = parseError;
                continue;
            }

            violation = ExplanationRubric.Violation(block, parsed);
            if (violation is null)
                return Assemble(block, parsed);
        }

        return Degrade(block, $"output rejected twice — last violation: {violation}");
    }

    /// <summary>
    /// T5: Code and Citations come from the pipeline VERBATIM — the invariant (citations ≥1)
    /// holds by construction, not by trusting the model. The LLM contributes what/why/link only;
    /// structural and semantic uncertainty merge (rubric row 5, automatic).
    /// </summary>
    private static Block Assemble(StructuralBlock structural, LlmExplanation e)
    {
        var uncertainty = structural.UncertaintyStructural is null
            ? e.UncertaintySemantic
            : StructuralNarrator.AppendSentence(structural.UncertaintyStructural, e.UncertaintySemantic ?? "");

        return BlockGuard.Ensure(new Block(
            Id: structural.Id,
            Title: structural.Title,
            Intent: structural.Intent,
            Code: structural.Code,
            Explanation: new Explanation(e.What, e.Why, e.Link, structural.Citations,
                string.IsNullOrWhiteSpace(uncertainty) ? null : uncertainty.Trim()),
            RelatedBlockIds: structural.RelatedBlockIds.Count > 0 ? structural.RelatedBlockIds : null,
            EstimatedMinutes: structural.EstimatedMinutes));
    }

    /// <summary>T7: degradation to facts — the review never stalls because of the LLM.</summary>
    private static Block Degrade(StructuralBlock structural, string reason)
    {
        var block = StructuralNarrator.ToBlock(structural);
        return BlockGuard.Ensure(block with
        {
            Explanation = block.Explanation with
            {
                Uncertainty = StructuralNarrator.AppendSentence(
                    block.Explanation.Uncertainty,
                    $"LLM explanation unavailable ({Truncate(reason)}).")
            }
        });
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
