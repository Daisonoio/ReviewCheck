using ReviewCheck.Core;

namespace ReviewCheck.Pipeline;

/// <summary>
/// The pipeline's output block (docs/24 §0.1): a <see cref="Block"/> missing only the LLM
/// narrative (what/why/link), but WITH the deterministic citations (grounding is born here —
/// GUARDRAILS G2: computed from the graph, not invented) and the graph facts the LLM must
/// anchor to. Plan #25 maps StructuralBlock → Block; the citations pass through untouched.
/// </summary>
public sealed record StructuralBlock(
    string Id,
    string Title,                              // deterministic, human-speaking label (the LLM can refine it)
    Intent Intent,                             // heuristic: Definition | Wiring | Config | Test | ...
    string Code,                               // the block's lines (hunk + minimal context)
    IReadOnlyList<Citation> Citations,         // DETERMINISTIC, ≥1 — real lines the block covers
    IReadOnlyList<string> RelatedBlockIds,
    IReadOnlyList<string> StructuralFacts,     // verifiable facts (symbols defined/used) for the LLM
    string? UncertaintyStructural,             // where the graph doesn't resolve (external symbol, no parse)
    int? EstimatedMinutes = null);

/// <summary>The deterministic result: ordered blocks + graph relations + seams.</summary>
public sealed record PipelineResult(
    string Title,
    IReadOnlyList<StructuralBlock> Blocks,             // already ordered (order = index)
    IReadOnlyList<BlockRelation> Relations,
    IReadOnlyList<InteractionPoint> InteractionPoints); // seams from the graph — never invented
