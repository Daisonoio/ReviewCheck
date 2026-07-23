using ReviewCheck.Core;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Mcp.Provider.Fixtures;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// T3 exit check (docs/23 §6): the fixture passes BlockGuard, declares ≥1 uncertainty,
/// ≥1 related_block_ids, mixed citation formats, seams pointing at real blocks, varied intents.
/// If the Core records change shape, these tests break the build — by design (docs/23 §3).
/// </summary>
public class StubProviderTests
{
    [Fact]
    public async Task Provider_ReturnsFixture_ForLocalSource()
    {
        var review = await new StubProvider().AnalyzeAsync(new Source.Local());

        Assert.Same(SampleCSharp.Review, review);
        Assert.True(review.Blocks.Count >= 3);
    }

    [Fact]
    public void EveryFixtureBlock_PassesBlockGuard()
    {
        foreach (var block in SampleCSharp.Review.Blocks)
            Assert.True(BlockGuard.IsValid(block), $"Block '{block.Id}' violates the invariant.");
    }

    [Fact]
    public void Fixture_DeclaresAtLeastOneUncertainty()
    {
        // Needed by recovery R3 ("any uncertainty?").
        Assert.Contains(SampleCSharp.Review.Blocks, b => b.Explanation.Uncertainty is not null);
    }

    [Fact]
    public void Fixture_HasRelatedBlockIds_PointingToExistingBlocks()
    {
        // Needed by recovery R4 ("what does it touch?") and R5 ("peek").
        var ids = SampleCSharp.Review.Blocks.Select(b => b.Id).ToHashSet();
        var withRelations = SampleCSharp.Review.Blocks
            .Where(b => b.RelatedBlockIds is { Count: > 0 })
            .ToList();

        Assert.NotEmpty(withRelations);
        foreach (var related in withRelations.SelectMany(b => b.RelatedBlockIds!))
            Assert.Contains(related, ids);
    }

    [Fact]
    public void Fixture_UsesBothCitationFormats()
    {
        var lines = SampleCSharp.Review.Blocks
            .SelectMany(b => b.Explanation.Citations)
            .Select(c => c.Lines)
            .ToList();

        Assert.Contains(lines, l => !l.Contains('-')); // single line: "8"
        Assert.Contains(lines, l => l.Contains('-'));  // range: "1-12"
    }

    [Fact]
    public void InteractionPoints_ReferenceRealBlockIds()
    {
        var ids = SampleCSharp.Review.Blocks.Select(b => b.Id).ToHashSet();

        Assert.NotEmpty(SampleCSharp.Review.InteractionPoints);
        foreach (var id in SampleCSharp.Review.InteractionPoints.SelectMany(p => p.BlockIds))
            Assert.Contains(id, ids);
    }

    [Fact]
    public void Fixture_CoversVariedIntents()
    {
        var intents = SampleCSharp.Review.Blocks.Select(b => b.Intent).ToHashSet();

        Assert.True(intents.Count >= 3, $"Only {intents.Count} distinct intents in the fixture.");
        Assert.Contains(Intent.Test, intents);
    }
}
