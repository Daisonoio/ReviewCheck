using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// The REAL provider (docs/24 §7 + docs/25 §11): local git diff → deterministic pipeline →
/// narrator seam (<see cref="IBlockNarrator"/>: LLM when configured, facts otherwise).
/// Replaces <see cref="StubProvider"/> behind the same <see cref="IReviewProvider"/> seam —
/// the 7 tools, the session store, and the agent do not change. Every block still passes
/// <see cref="BlockGuard.Ensure"/>: the co-presence + grounding guarantee is identical.
/// </summary>
public sealed class PipelineProvider(IDiffReader diffReader, AnalysisPipeline pipeline, IBlockNarrator narrator)
    : IReviewProvider
{
    public async Task<AnalyzedReview> AnalyzeAsync(Source source)
    {
        if (source is not Source.Local local)
            throw new InvalidOperationException(
                "Mode B (pull request) is not included in the MVP: review the local diff instead.");

        var diff = diffReader.Read(local.Ref);
        var structural = pipeline.Run(diff);

        var blocks = (await narrator.NarrateAsync(structural.Blocks))
            .Select(BlockGuard.Ensure)
            .ToList();

        var estimated = blocks.Sum(b => b.EstimatedMinutes ?? 0);
        return new AnalyzedReview(
            structural.Title,
            blocks,
            structural.InteractionPoints,
            estimated > 0 ? estimated : null);
    }
}
