using ReviewCheck.Llm;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 T3 gate: the prompt contains the code, the fixed citations, the facts, and the related
/// blocks' code — a specificity directive — and no invitation to a verdict (the stop-if).
/// </summary>
public sealed class PromptBuilderTests
{
    private static readonly IReadOnlyDictionary<string, StructuralBlock> NoMap =
        new Dictionary<string, StructuralBlock>();

    [Fact]
    public void UserPrompt_CarriesCodeCitationsAndFacts()
    {
        var prompt = PromptBuilder.User(Sample.Block(), NoMap);

        Assert.Contains("public string Hello", prompt);
        Assert.Contains("src/Greeter.cs lines 5", prompt);
        Assert.Contains("defines 'Greeter.Hello'", prompt);
        Assert.Contains("used by 'Program.cs — top-level changes'", prompt);
    }

    [Fact]
    public void UserPrompt_CarriesRelatedBlockCode_WhenMapped()
    {
        var self = Sample.Block(); // RelatedBlockIds = ["b2"]
        var related = self with { Id = "b2", Title = "Method Program.Main — added", Code = "Console.WriteLine(new Greeter().Hello(\"x\"));" };
        var byId = new Dictionary<string, StructuralBlock> { [self.Id] = self, [related.Id] = related };

        var prompt = PromptBuilder.User(self, byId);

        Assert.Contains("RELATED BLOCKS", prompt);
        Assert.Contains("Method Program.Main — added", prompt);
        Assert.Contains("new Greeter().Hello", prompt);
    }

    [Fact]
    public void UserPrompt_CarriesStructuralUncertainty_WhenPresent()
    {
        var prompt = PromptBuilder.User(Sample.Block(uncertainty: "Unresolved symbols: 'Registry'."), NoMap);

        Assert.Contains("STRUCTURAL UNCERTAINTY", prompt);
        Assert.Contains("Unresolved symbols: 'Registry'.", prompt);
    }

    [Fact]
    public void SystemPrompt_ForbidsVerdicts_AndAsksForJson()
    {
        var system = PromptBuilder.System();

        Assert.Contains("don't judge", system);
        Assert.Contains("uncertainty_semantic", system);
        Assert.Contains("\"what\"", system);
    }

    [Fact]
    public void SystemPrompt_DemandsSpecificity_WithAnExample()
    {
        var system = PromptBuilder.System();

        Assert.Contains("Be specific", system);
        Assert.Contains("Name the actual identifiers", system);
        Assert.Contains("STRONG", system); // the few-shot anchor
    }

    [Fact]
    public void SystemPrompt_NeverAsksForAJudgment()
    {
        // T3 stop-if: the prompt must not invite evaluation. (The rules NAME the banned words —
        // "never say correct/safe/approved" — so we check for requesting phrases, not the words.)
        var everything = PromptBuilder.System() + PromptBuilder.User(Sample.Block(), NoMap);

        Assert.DoesNotContain("is it correct", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("judge whether", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("find bugs", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryPrompt_QuotesTheViolationFirst()
    {
        var retry = PromptBuilder.System("verdict language ('correct')");

        Assert.StartsWith("PREVIOUS ATTEMPT REJECTED: verdict language ('correct').", retry);
        Assert.Contains("don't judge", retry); // the full rules still follow
    }
}
