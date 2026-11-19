using ReviewCheck.Llm;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 T6 gate: the rubric rejects verdicts, invented references, and empty narrative —
/// and lets an honest grounded explanation through. Stop-if: an evaluative output passes.
/// A reference to a RELATED block (whose code is in the prompt) is legitimate, not a hallucination.
/// </summary>
public sealed class ExplanationRubricTests
{
    private static readonly StructuralBlock[] NoRelated = [];

    private static LlmExplanation Valid() => new(
        What: "Adds a Hello method on Greeter that formats a greeting.",
        Why: "It provides the greeting used by the entry point.",
        Link: "Used by 'Program.cs — top-level changes'.",
        UncertaintySemantic: null);

    private static string? Check(LlmExplanation e, params StructuralBlock[] related) =>
        ExplanationRubric.Violation(Sample.Block(), e, related);

    [Fact]
    public void GroundedExplanation_Passes()
    {
        Assert.Null(Check(Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Adds.")]
    public void TrivialWhat_IsRejected(string what)
    {
        Assert.Contains("'what'", Check(Valid() with { What = what }));
    }

    [Fact]
    public void MissingLink_IsRejected()
    {
        Assert.Contains("'link'", Check(Valid() with { Link = "" }));
    }

    [Theory]
    [InlineData("The implementation is correct and does what it should.")]
    [InlineData("This method is safe to use in production.")]
    [InlineData("There is a bug in the formatting.")]
    [InlineData("This code is approved.")]
    public void VerdictLanguage_IsRejected(string why)
    {
        Assert.Contains("verdict language", Check(Valid() with { Why = why }));
    }

    [Fact]
    public void DoubtInUncertainty_IsAllowed_ItIsTheHonestyChannel()
    {
        var e = Valid() with { UncertaintySemantic = "I cannot tell whether the caller expects a trailing space." };
        Assert.Null(Check(e));
    }

    [Fact]
    public void InventedFileReference_IsRejected()
    {
        var e = Valid() with { Link = "Also interacts with PaymentService.cs during checkout." };
        Assert.Contains("PaymentService.cs", Check(e));
    }

    [Fact]
    public void KnownFileReference_IsAllowed()
    {
        var e = Valid() with { What = "Adds a Hello method in src/Greeter.cs that formats a greeting." };
        Assert.Null(Check(e));
    }

    [Fact]
    public void RelatedBlockFileReference_IsAllowed()
    {
        // The related block's code is in the prompt, so naming its file is grounded, not invented.
        var related = Sample.Block() with
        {
            Id = "b2",
            Title = "Method Program.Main — added",
            Citations = [new Core.Citation("src/Program.cs", "3")],
            Code = "new Greeter().Hello(\"x\");",
        };
        var e = Valid() with { Link = "Called from src/Program.cs where Greeter.Hello builds the message." };

        Assert.Null(Check(e, related));
    }

    [Fact]
    public void WordsContainingBannedStems_AreNotFalsePositives()
    {
        // "debug" contains "bug" but is not the word "bug".
        var e = Valid() with { Why = "It mirrors the debug output the entry point already prints." };
        Assert.Null(Check(e));
    }

    // ---- CountVerdictLanguage (GUARDRAILS.md §4 oversight signal) — same vocabulary as Violation ----

    [Fact]
    public void CountVerdictLanguage_CleanText_IsZero()
    {
        Assert.Equal(0, ExplanationRubric.CountVerdictLanguage("Adds a Hello method that formats a greeting."));
    }

    [Fact]
    public void CountVerdictLanguage_CountsEachOccurrence()
    {
        Assert.Equal(2, ExplanationRubric.CountVerdictLanguage("This is correct and safe."));
    }
}
