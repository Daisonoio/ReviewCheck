using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp.Provider.Fixtures;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// MVP-1 provider: ignores the source and returns the fixture, but runs every block
/// through <see cref="BlockGuard.Ensure"/> first — the same choke point the real
/// pipeline will pass through, so the tools' guarantees hold from day one.
/// The fixture is already narrated, so the <paramref name="narrator"/> is unused here.
/// </summary>
public sealed class StubProvider : IReviewProvider
{
    public Task<AnalyzedReview> AnalyzeAsync(Source source, IBlockNarrator narrator)
    {
        var review = SampleCSharp.Review;
        foreach (var block in review.Blocks)
            BlockGuard.Ensure(block);
        return Task.FromResult(review);
    }
}
