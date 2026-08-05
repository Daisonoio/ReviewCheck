using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// The REAL provider (docs/24 §7 + docs/25 §11): local git diff → deterministic pipeline →
/// the per-request narrator (<see cref="IBlockNarrator"/>: key LLM / host sampling / facts,
/// chosen by <see cref="NarratorResolver"/>). Replaces <see cref="StubProvider"/> behind the same
/// <see cref="IReviewProvider"/> seam — the 7 tools, the session store, and the agent do not
/// change. Every block still passes <see cref="BlockGuard.Ensure"/>: the co-presence + grounding
/// guarantee is identical.
/// </summary>
public sealed class PipelineProvider(
    IDiffReader diffReader, AnalysisPipeline pipeline, IPullRequestPlatform pullRequestPlatform)
    : IReviewProvider
{
    public async Task<AnalyzedReview> AnalyzeAsync(Source source, IBlockNarrator narrator)
    {
        // Same LocalDiffResult shape either way (GitHubDiffReader builds one from the platform seam
        // exactly like LocalDiffReader builds one from git) — the pipeline below never knows which
        // source it got. Only the title's label differs.
        var (diff, label) = source switch
        {
            Source.Local local => (diffReader.Read(local.Ref), "Local changes"),
            Source.PullRequest { Platform: "github" } pr =>
                (await new GitHubDiffReader(pullRequestPlatform, pr.Repo, pr.Pr).ReadAsync(), $"Pull request #{pr.Pr}"),
            Source.PullRequest pr => throw new InvalidOperationException(
                $"Platform '{pr.Platform}' is not supported yet — only 'github' reviews a remote pull request today."),
            _ => throw new InvalidOperationException($"Unknown review source: {source.GetType().Name}."),
        };

        var structural = pipeline.Run(diff, label);

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
