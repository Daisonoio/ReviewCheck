using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;

namespace ReviewCheck.Llm.Tests;

/// <summary>
/// Docs/25 §7 key tests (T5–T8): valid output → Block with the PIPELINE's citations; invalid or
/// verdict output → retried once, then degraded to facts; LLM down → degraded immediately.
/// The invariant under test: a valid Block ALWAYS comes out, and the LLM never touches
/// code or citations.
/// </summary>
public sealed class LlmAdapterTests
{
    private static async Task<Block> One(FakeLlmProvider fake, StructuralBlock? block = null)
    {
        var result = await new LlmAdapter(fake).ExplainAsync([block ?? Sample.Block()]);
        return Assert.Single(result);
    }

    // ---- T5: staple, never trust ----

    [Fact]
    public async Task ValidOutput_BuildsBlock_WithPipelineCitationsVerbatim()
    {
        var structural = Sample.Block();
        var block = await One(new FakeLlmProvider().Returns(Sample.ValidJson), structural);

        Assert.True(BlockGuard.IsValid(block));
        Assert.Same(structural.Citations, block.Explanation.Citations); // verbatim — not rebuilt, not filtered
        Assert.Equal(structural.Code, block.Code);
        Assert.StartsWith("Adds a Hello method", block.Explanation.What);
    }

    [Fact]
    public async Task LlmCitations_CannotEnterTheBlock_EvenIfItInventsThem()
    {
        // The wire shape has no citations field at all — but even a reply smuggling one in
        // changes nothing: the assembler only reads what/why/link/uncertainty_semantic.
        var smuggling =
            """
            {"what": "Adds a Hello method on Greeter that formats a greeting.",
             "why": "It provides the greeting used by the entry point.",
             "link": "Used by 'Program.cs — top-level changes'.",
             "citations": [{"file": "invented.cs", "lines": "1-999"}],
             "uncertainty_semantic": null}
            """;
        var structural = Sample.Block();
        var block = await One(new FakeLlmProvider().Returns(smuggling), structural);

        Assert.Same(structural.Citations, block.Explanation.Citations);
    }

    // ---- T6: retry once with the violation quoted ----

    [Fact]
    public async Task VerdictOutput_IsRetriedOnce_SecondValidAnswerWins()
    {
        var verdict = Sample.ValidJson.Replace("Adds a Hello method on Greeter", "This correct code adds a Hello method");
        var fake = new FakeLlmProvider().Returns(verdict).Returns(Sample.ValidJson);

        var block = await One(fake);

        Assert.Equal(2, fake.Calls.Count);
        Assert.StartsWith("PREVIOUS ATTEMPT REJECTED", fake.Calls[1].System);
        Assert.Contains("verdict language", fake.Calls[1].System);
        Assert.StartsWith("Adds a Hello method", block.Explanation.What);
        Assert.DoesNotContain("LLM explanation unavailable", block.Explanation.Uncertainty ?? "");
    }

    // ---- T7: degradation to facts — always a valid Block ----

    [Fact]
    public async Task RejectedTwice_DegradesToFacts_StillValidAndHonest()
    {
        var invented = Sample.ValidJson.Replace("Used by 'Program.cs — top-level changes'.",
            "Also touches PaymentService.cs.");
        var fake = new FakeLlmProvider().Returns(invented).Returns(invented);

        var block = await One(fake);

        Assert.True(BlockGuard.IsValid(block));
        Assert.StartsWith("Defines 'Greeter.Hello'", block.Explanation.What); // facts narrative
        Assert.Contains("LLM explanation unavailable", block.Explanation.Uncertainty);
        Assert.Contains("PaymentService.cs", block.Explanation.Uncertainty); // the reason names the violation
    }

    [Fact]
    public async Task LlmDown_DegradesImmediately_NoRetryAgainstADeadEndpoint()
    {
        var fake = new FakeLlmProvider().Fails("connection refused");

        var block = await One(fake);

        Assert.Single(fake.Calls);
        Assert.True(BlockGuard.IsValid(block));
        Assert.Contains("LLM explanation unavailable", block.Explanation.Uncertainty);
    }

    [Fact]
    public async Task UnparseableTwice_DegradesToFacts()
    {
        var fake = new FakeLlmProvider().Returns("no json here").Returns("still no json");

        var block = await One(fake);

        Assert.True(BlockGuard.IsValid(block));
        Assert.Contains("LLM explanation unavailable", block.Explanation.Uncertainty);
    }

    // ---- Rubric row 5: structural uncertainty always carried over ----

    [Fact]
    public async Task StructuralUncertainty_AppearsInTheFinalExplanation()
    {
        var structural = Sample.Block(uncertainty: "Unresolved symbols: 'Registry'.");
        var block = await One(new FakeLlmProvider().Returns(Sample.ValidJson), structural);

        Assert.Contains("Unresolved symbols: 'Registry'.", block.Explanation.Uncertainty);
    }

    [Fact]
    public async Task SemanticUncertainty_MergesAfterStructural()
    {
        var withSemantic = Sample.ValidJson.Replace(
            "\"uncertainty_semantic\": null",
            "\"uncertainty_semantic\": \"Unsure of the trailing space.\"");
        var structural = Sample.Block(uncertainty: "Unresolved symbols: 'Registry'.");

        var block = await One(new FakeLlmProvider().Returns(withSemantic), structural);

        Assert.Contains("Unresolved symbols: 'Registry'.", block.Explanation.Uncertainty);
        Assert.Contains("Unsure of the trailing space.", block.Explanation.Uncertainty);
    }

    // ---- T8: e2e over REAL pipeline output ----

    [Fact]
    public async Task EndToEnd_RealStructuralBlocks_AllCoPresentAndGrounded()
    {
        var diff = new LocalDiffResult("working",
        [
            new FileDiff("src/Greeter.cs", FileChangeKind.Added,
                [new DiffHunk(1, 6, 1, 6,
                    Enumerable.Range(1, 6).Select(n => new DiffLine('+', $"l{n}", null, n)).ToList())],
                NewText: "namespace App;\n\npublic class Greeter\n{\n    public string Hello(string name) => $\"Hello {name}\";\n}\n"),
        ]);
        var structural = new AnalysisPipeline().Run(diff);
        Assert.NotEmpty(structural.Blocks);

        // An answer grounded in THIS block's material (Sample.ValidJson mentions Program.cs,
        // which the rubric would rightly reject here); any further blocks degrade.
        const string grounded =
            """
            {"what": "Adds a Hello method on Greeter that formats a greeting for the given name.",
             "why": "It introduces the greeting behavior this change is about.",
             "link": "No graph relations to other blocks in this diff.",
             "uncertainty_semantic": null}
            """;
        var fake = new FakeLlmProvider().Returns(grounded);
        for (var i = 1; i < structural.Blocks.Count; i++)
            fake.Fails();

        var blocks = await new LlmAdapter(fake).ExplainAsync(structural.Blocks);

        Assert.Equal(structural.Blocks.Count, blocks.Count);
        Assert.All(blocks, b => Assert.True(BlockGuard.IsValid(b), $"'{b.Title}' violates co-presence/grounding"));
        Assert.All(blocks.Zip(structural.Blocks), pair =>
            Assert.Equal(pair.Second.Citations, pair.First.Explanation.Citations));
    }
}
