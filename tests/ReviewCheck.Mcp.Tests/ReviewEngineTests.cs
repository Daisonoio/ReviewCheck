using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// T5–T8 exit checks (docs/23 §6) against the stub, plus the recovery semantics R1–R10 rely on.
/// Every assertion is about behavior the tools GUARANTEE (co-presence, pointer discipline, counters,
/// outcome = sum of decisions), so the same guarantees back the R1–R10 commands.
/// </summary>
public sealed class ReviewEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-eng-" + Guid.NewGuid().ToString("N"));

    private ReviewEngine NewEngine() => new(new StubProvider(), new SessionStore(_root));

    private async Task<string> OpenSession(ReviewEngine engine)
        => (await engine.GetReviewPlanAsync(new Source.Local("working"))).Session;

    // ---- T5: get_review_plan ----

    [Fact]
    public async Task GetReviewPlan_OpensSession_AndReturnsCoPresentFirstBlock()
    {
        var plan = await NewEngine().GetReviewPlanAsync(new Source.Local("working"));

        Assert.False(string.IsNullOrWhiteSpace(plan.Session));
        Assert.NotEmpty(plan.Blocks);
        Assert.Equal(0, plan.Blocks[0].OrderIndex);
        Assert.NotEmpty(plan.InteractionPoints);

        // Co-presence + grounding on the first block.
        Assert.False(string.IsNullOrWhiteSpace(plan.FirstBlock.Code));
        Assert.NotEmpty(plan.FirstBlock.Explanation.Citations);
    }

    // ---- T6: next_block advances, get_block does not ----

    [Fact]
    public async Task NextBlock_Advances_WhileGetBlock_DoesNot()
    {
        var engine = NewEngine();
        var session = await OpenSession(engine);

        var firstStatus = engine.ReviewStatus(session);
        var current = firstStatus.CurrentBlockId!;

        // get_block on a RELATED block must not move the pointer (R5 peek).
        var related = engine.GetBlock(session, current).Block.RelatedBlockIds![0];
        engine.GetBlock(session, related);
        Assert.Equal(current, engine.ReviewStatus(session).CurrentBlockId);

        // next_block DOES move it.
        var next = engine.NextBlock(session);
        Assert.NotEqual(current, next.Block.Id);
        Assert.Equal(next.Block.Id, engine.ReviewStatus(session).CurrentBlockId);
        Assert.Equal(2, next.Position.Index);
    }

    [Fact]
    public async Task GetBlock_ReturnsCoPresentBlock_WithGroundingAndEdges()
    {
        var engine = NewEngine();
        var session = await OpenSession(engine);
        var firstId = engine.ReviewStatus(session).CurrentBlockId!;

        var block = engine.GetBlock(session, firstId).Block;

        Assert.False(string.IsNullOrWhiteSpace(block.Code));       // R1 co-presence
        Assert.NotEmpty(block.Explanation.Citations);              // R2 grounding
        Assert.NotNull(block.Explanation.Uncertainty);            // R3 (first fixture block declares it)
        Assert.NotEmpty(block.RelatedBlockIds!);                   // R4 edges
    }

    [Fact]
    public async Task GetBlock_UnknownId_Throws()
    {
        var engine = NewEngine();
        var session = await OpenSession(engine);
        Assert.Throws<BlockNotFoundException>(() => engine.GetBlock(session, "nope"));
    }

    // ---- T7: accept / request_correction / review_status ----

    [Fact]
    public async Task Decisions_AreRecorded_AndCounted()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());
        var session = plan.Session;
        var ids = plan.Blocks.Select(b => b.Id).ToList();

        engine.AcceptBlock(session, ids[0]);
        var corr = engine.RequestCorrection(session, ids[1], "rename the field");

        Assert.True(corr.Ok);
        Assert.Equal("correction_requested", corr.BlockStatus);
        Assert.StartsWith("n-", corr.NoteId);

        var status = engine.ReviewStatus(session);
        Assert.Equal(1, status.Accepted);
        Assert.Equal(1, status.Corrections);
        Assert.Equal(ids.Count, status.Total);
        Assert.True(status.ProgressPct > 0);
    }

    // ---- T8: submit_review ----

    [Fact]
    public async Task Submit_WithUndecidedBlocks_DoesNotClose()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());
        engine.AcceptBlock(plan.Session, plan.Blocks[0].Id); // leave the rest undecided

        var result = engine.SubmitReview(plan.Session, confirm: true);

        Assert.False(result.Posted);
        Assert.NotNull(result.UndecidedBlocks);
        Assert.NotEmpty(result.UndecidedBlocks!);
        Assert.NotEqual("ready_to_proceed", result.Outcome);
    }

    [Fact]
    public async Task Submit_AllAccepted_IsReadyToProceed_AndPostsNothing()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());
        foreach (var b in plan.Blocks)
            engine.AcceptBlock(plan.Session, b.Id);

        var result = engine.SubmitReview(plan.Session, confirm: false);

        Assert.Equal("ready_to_proceed", result.Outcome);
        Assert.False(result.Posted);
        Assert.Null(result.UndecidedBlocks);
    }

    [Fact]
    public async Task Submit_WithCorrection_ListsCorrectionsToApply()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());
        foreach (var b in plan.Blocks)
            engine.AcceptBlock(plan.Session, b.Id);
        engine.RequestCorrection(plan.Session, plan.Blocks[1].Id, "extract a constant");

        var result = engine.SubmitReview(plan.Session, confirm: false);

        Assert.Equal("corrections_to_apply", result.Outcome);
        Assert.False(result.Posted);
        Assert.NotNull(result.Notes);
        Assert.Contains(result.Notes!, n => n.Note == "extract a constant");
    }

    [Fact]
    public async Task Submit_PullRequestSource_IsOutOfScope_AndPostsNothing()
    {
        var engine = NewEngine();
        // Open a PR-source session directly through the store so submit sees the reserved PR seam.
        var store = new SessionStore(_root);
        var analyzed = await new StubProvider().AnalyzeAsync(new Source.Local(), new FactsNarrator());
        var session = store.Create(analyzed, new Source.PullRequest("github", "org/repo", "42"));
        foreach (var b in analyzed.Blocks)
            store.SetStatus(session, b.Id, BlockStatus.Accepted);

        var result = engine.SubmitReview(session, confirm: true);

        Assert.Equal("comment_only", result.Outcome);
        Assert.False(result.Posted);
    }

    // ---- review_health: local oversight signals (GUARDRAILS.md §4) ----

    [Fact]
    public async Task ReviewHealth_CountsDecisions_AndFindsNoGroundingGaps()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());
        var ids = plan.Blocks.Select(b => b.Id).ToList();

        engine.AcceptBlock(plan.Session, ids[0]);
        engine.RequestCorrection(plan.Session, ids[1], "rename the field");

        var health = engine.ReviewHealth(plan.Session);

        Assert.Equal(ids.Count, health.TotalBlocks);
        Assert.Equal(1, health.Accepted);
        Assert.Equal(1, health.Corrections);
        Assert.Equal(ids.Count - 2, health.Pending);
        Assert.Equal(50.0, health.CorrectionRatePct); // 1 correction / (1 accepted + 1 correction)

        // BlockGuard guarantees grounding at construction — the stub fixture must show 0 gaps.
        Assert.Equal(0, health.UngroundedBlocks);
        Assert.Equal(0.0, health.UngroundedPct);
    }

    [Fact]
    public async Task ReviewHealth_NothingDecidedYet_CorrectionRateIsNull()
    {
        var engine = NewEngine();
        var plan = await engine.GetReviewPlanAsync(new Source.Local());

        var health = engine.ReviewHealth(plan.Session);

        Assert.Equal(0, health.Accepted);
        Assert.Equal(0, health.Corrections);
        Assert.Null(health.CorrectionRatePct); // nothing decided — not "0% correction rate"
    }

    [Fact]
    public async Task ReviewHealth_FlagsVerdictLanguage_UsingTheSameRubricVocabulary()
    {
        var engine = NewEngine();
        var store = new SessionStore(_root);
        var plan = await engine.GetReviewPlanAsync(new Source.Local());

        // Simulate a narrator slipping evaluative language past the seam into stored state.
        var state = store.Load(plan.Session);
        var tainted = state.Blocks[0] with
        {
            Explanation = state.Blocks[0].Explanation with { Why = "This implementation is correct and safe." },
        };
        var blocks = state.Blocks.ToList();
        blocks[0] = tainted;
        store.Save(state with { Blocks = blocks });

        var health = engine.ReviewHealth(plan.Session);

        Assert.Equal(2, health.EvaluativeLanguageHits); // "correct" + "safe"
        Assert.Contains(tainted.Id, health.FlaggedBlockIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
