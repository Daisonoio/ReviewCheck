using ReviewCheck.Core;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm;

/// <summary>
/// The narrative seam: turns the pipeline's structural blocks into complete <see cref="Block"/>s.
/// Two implementations — <see cref="LlmAdapter"/> (LLM narrative, reined in) and
/// <see cref="FactsNarrator"/> (deterministic floor). <c>PipelineProvider</c> depends on this,
/// not on either implementation: swapping narration never touches the tools (docs/23 §0 spirit).
/// Contract: every returned block passes <c>BlockGuard</c> — narration can degrade, never fail.
/// </summary>
public interface IBlockNarrator
{
    Task<IReadOnlyList<Block>> NarrateAsync(IReadOnlyList<StructuralBlock> blocks, CancellationToken ct = default);
}

/// <summary>The deterministic implementation: structural facts only, no LLM anywhere.</summary>
public sealed class FactsNarrator : IBlockNarrator
{
    public Task<IReadOnlyList<Block>> NarrateAsync(IReadOnlyList<StructuralBlock> blocks, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Block>>(
            blocks.Select(b => BlockGuard.Ensure(StructuralNarrator.ToBlock(b))).ToList());
}
