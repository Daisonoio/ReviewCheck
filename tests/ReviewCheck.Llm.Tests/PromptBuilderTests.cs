using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 T3 gate: the prompt contains the code, the fixed citations, and the facts — and no
/// invitation to a verdict (the stop-if).
/// </summary>
public sealed class PromptBuilderTests
{
    [Fact]
    public void UserPrompt_CarriesCodeCitationsAndFacts()
    {
        var prompt = PromptBuilder.User(Sample.Block());

        Assert.Contains("public string Hello", prompt);
        Assert.Contains("src/Greeter.cs lines 5", prompt);
        Assert.Contains("defines 'Greeter.Hello'", prompt);
        Assert.Contains("used by 'Program.cs — top-level changes'", prompt);
    }

    [Fact]
    public void UserPrompt_CarriesStructuralUncertainty_WhenPresent()
    {
        var prompt = PromptBuilder.User(Sample.Block(uncertainty: "Unresolved symbols: 'Registry'."));

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
    public void SystemPrompt_NeverAsksForAJudgment()
    {
        // T3 stop-if: the prompt must not invite evaluation. (The rules NAME the banned words —
        // "never say correct/safe/approved" — so we check for requesting phrases, not the words.)
        var everything = PromptBuilder.System() + PromptBuilder.User(Sample.Block());

        Assert.DoesNotContain("is it correct", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("judge whether", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("find bugs", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assess", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetryPrompt_QuotesTheViolationFirst()
    {
        var retry = PromptBuilder.System("verdict language ('correct')");

        Assert.StartsWith("PREVIOUS ATTEMPT REJECTED: verdict language ('correct').", retry);
        Assert.Contains("don't judge", retry); // the full rules still follow
    }
}
