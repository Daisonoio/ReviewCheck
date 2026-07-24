using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 T6 gate: the rubric rejects verdicts, invented references, and empty narrative —
/// and lets an honest grounded explanation through. Stop-if: an evaluative output passes.
/// </summary>
public sealed class ExplanationRubricTests
{
    private static LlmExplanation Valid() => new(
        What: "Adds a Hello method on Greeter that formats a greeting.",
        Why: "It provides the greeting used by the entry point.",
        Link: "Used by 'Program.cs — top-level changes'.",
        UncertaintySemantic: null);

    [Fact]
    public void GroundedExplanation_Passes()
    {
        Assert.Null(ExplanationRubric.Violation(Sample.Block(), Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Adds.")]
    public void TrivialWhat_IsRejected(string what)
    {
        var violation = ExplanationRubric.Violation(Sample.Block(), Valid() with { What = what });
        Assert.Contains("'what'", violation);
    }

    [Fact]
    public void MissingLink_IsRejected()
    {
        var violation = ExplanationRubric.Violation(Sample.Block(), Valid() with { Link = "" });
        Assert.Contains("'link'", violation);
    }

    [Theory]
    [InlineData("The implementation is correct and does what it should.")]
    [InlineData("This method is safe to use in production.")]
    [InlineData("There is a bug in the formatting.")]
    [InlineData("This code is approved.")]
    public void VerdictLanguage_IsRejected(string why)
    {
        var violation = ExplanationRubric.Violation(Sample.Block(), Valid() with { Why = why });
        Assert.Contains("verdict language", violation);
    }

    [Fact]
    public void DoubtInUncertainty_IsAllowed_ItIsTheHonestyChannel()
    {
        var e = Valid() with { UncertaintySemantic = "I cannot tell whether the caller expects a trailing space." };
        Assert.Null(ExplanationRubric.Violation(Sample.Block(), e));
    }

    [Fact]
    public void InventedFileReference_IsRejected()
    {
        var e = Valid() with { Link = "Also interacts with PaymentService.cs during checkout." };
        var violation = ExplanationRubric.Violation(Sample.Block(), e);
        Assert.Contains("PaymentService.cs", violation);
    }

    [Fact]
    public void KnownFileReference_IsAllowed()
    {
        var e = Valid() with { What = "Adds a Hello method in src/Greeter.cs that formats a greeting." };
        Assert.Null(ExplanationRubric.Violation(Sample.Block(), e));
    }

    [Fact]
    public void WordsContainingBannedStems_AreNotFalsePositives()
    {
        // "debug" contains "bug" but is not the word "bug".
        var e = Valid() with { Why = "It mirrors the debug output the entry point already prints." };
        Assert.Null(ExplanationRubric.Violation(Sample.Block(), e));
    }
}
