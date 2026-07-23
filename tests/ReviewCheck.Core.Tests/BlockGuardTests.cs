using ReviewCheck.Core;

namespace ReviewCheck.Core.Tests;

/// <summary>
/// Gate G-SCHEMA (docs/22 §0): a Block without code or without ≥1 citation is not valid.
/// Positive test: a well-formed block passes. Negative tests: every invariant violation throws.
/// </summary>
public class BlockGuardTests
{
    private static Explanation ValidExplanation() =>
        new(
            What: "Declares the token-bucket limiter, the core of the change.",
            Why: "Decides whether a request passes or is rejected.",
            Link: "Used by the middleware (block b3); parameters from block b2.",
            Citations: [new Citation("RateLimiting/TokenBucket.cs", "1-15")]);

    private static Block ValidBlock() =>
        new(
            Id: "b1",
            Title: "TokenBucket class",
            Intent: Intent.Definition,
            Code: "public sealed class TokenBucket\n{\n    public bool Allow(string key) { ... }\n}",
            Explanation: ValidExplanation(),
            RelatedBlockIds: ["b2", "b3"]);

    [Fact]
    public void ValidBlock_PassesAndIsReturnedUnchanged()
    {
        var block = ValidBlock();

        Assert.Same(block, BlockGuard.Ensure(block));
        Assert.True(BlockGuard.IsValid(block));
    }

    [Fact]
    public void Block_WithoutCode_Fails()
    {
        var block = ValidBlock() with { Code = "" };

        var ex = Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(block));
        Assert.Contains("co-presence", ex.Message);
        Assert.False(BlockGuard.IsValid(block));
    }

    [Fact]
    public void Block_WithWhitespaceCode_Fails()
    {
        var block = ValidBlock() with { Code = "   \n  " };

        Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(block));
    }

    [Fact]
    public void Block_WithoutCitations_Fails()
    {
        var block = ValidBlock() with
        {
            Explanation = ValidExplanation() with { Citations = [] },
        };

        var ex = Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(block));
        Assert.Contains("grounding", ex.Message);
    }

    [Fact]
    public void Block_WithEmptyCitationFields_Fails()
    {
        var block = ValidBlock() with
        {
            Explanation = ValidExplanation() with { Citations = [new Citation("", "12")] },
        };

        Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(block));
    }

    [Fact]
    public void Block_WithEmptyWhatOrWhy_Fails()
    {
        var noWhat = ValidBlock() with { Explanation = ValidExplanation() with { What = "" } };
        var noWhy = ValidBlock() with { Explanation = ValidExplanation() with { Why = " " } };

        Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(noWhat));
        Assert.Throws<InvalidBlockException>(() => BlockGuard.Ensure(noWhy));
    }

    [Fact]
    public void Block_WithNullUncertainty_IsStillValid()
    {
        // Uncertainty is nullable by contract: null means "no uncertainty declared".
        var block = ValidBlock();

        Assert.Null(block.Explanation.Uncertainty);
        Assert.True(BlockGuard.IsValid(block));
    }

    [Fact]
    public void Block_WithDeclaredUncertainty_IsValidAndPreserved()
    {
        var block = ValidBlock() with
        {
            Explanation = ValidExplanation() with { Uncertainty = "Refill is defined in another partial file." },
        };

        Assert.True(BlockGuard.IsValid(block));
        Assert.Equal("Refill is defined in another partial file.", block.Explanation.Uncertainty);
    }
}
