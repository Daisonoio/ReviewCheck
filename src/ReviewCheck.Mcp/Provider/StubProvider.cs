using ReviewCheck.Core;
using ReviewCheck.Mcp.Provider.Fixtures;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// MVP-1 provider: ignores the source and returns the fixture, but runs every block
/// through <see cref="BlockGuard.Ensure"/> first — the same choke point the real
/// pipeline will pass through, so the tools' guarantees hold from day one.
/// </summary>
public sealed class StubProvider : IReviewProvider
{
    public Task<AnalyzedReview> AnalyzeAsync(Source source)
    {
        var review = SampleCSharp.Review;
        foreach (var block in review.Blocks)
            BlockGuard.Ensure(block);
        return Task.FromResult(review);
    }
}
