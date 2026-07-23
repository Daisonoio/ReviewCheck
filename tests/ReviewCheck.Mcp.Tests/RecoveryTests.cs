using System.Reflection;
using ReviewCheck.Core;
using ReviewCheck.Mcp;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// T9 (docs/23 §6) + the recovery protocol of docs/21 Part 3. Each test maps one recovery
/// command R1–R10 to the tool it triggers and the field the tool GUARANTEES, proving the
/// soft-guardrail override is reliable against the stub. Recovery never mutates the review:
/// it does not decide, does not advance, does not post.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-rec-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewEngine _engine;

    public RecoveryTests() => _engine = new ReviewEngine(new StubProvider(), new SessionStore(_root));

    private async Task<(string Session, IReadOnlyList<BlockSummaryView> Blocks)> Open()
    {
        var plan = await _engine.GetReviewPlanAsync(new Source.Local("working"));
        return (plan.Session, plan.Blocks);
    }

    [Fact] // R1 — Co-presence: "show the code" → get_block → block.code (required)
    public async Task R1_CoPresence()
    {
        var (session, blocks) = await Open();
        var block = _engine.GetBlock(session, blocks[0].Id).Block;
        Assert.False(string.IsNullOrWhiteSpace(block.Code));
    }

    [Fact] // R2 — Grounding: "with citations" → get_block → citations (minItems 1)
    public async Task R2_Grounding()
    {
        var (session, blocks) = await Open();
        var block = _engine.GetBlock(session, blocks[0].Id).Block;
        Assert.NotEmpty(block.Explanation.Citations);
        Assert.All(block.Explanation.Citations, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.File));
            Assert.False(string.IsNullOrWhiteSpace(c.Lines));
        });
    }

    [Fact] // R3 — Declared uncertainty: "uncertainty?" → get_block → explanation.uncertainty (shown, or "none")
    public async Task R3_Uncertainty()
    {
        var (session, blocks) = await Open();
        var withUncertainty = _engine.GetBlock(session, blocks[0].Id).Block;   // fixture b1 declares it
        Assert.False(string.IsNullOrWhiteSpace(withUncertainty.Explanation.Uncertainty));

        var withoutUncertainty = _engine.GetBlock(session, blocks[1].Id).Block; // fixture b2 has none
        Assert.Null(withoutUncertainty.Explanation.Uncertainty);                // agent renders "no uncertainty declared"
    }

    [Fact] // R4 — Visible edges: "what does it touch?" → get_block → link + related_block_ids
    public async Task R4_Edges()
    {
        var (session, blocks) = await Open();
        var block = _engine.GetBlock(session, blocks[0].Id).Block;
        Assert.False(string.IsNullOrWhiteSpace(block.Explanation.Link));
        Assert.NotEmpty(block.RelatedBlockIds!);
    }

    [Fact] // R5 — Peek without advancing: get_block(related) then review_status → current unchanged
    public async Task R5_PeekWithoutAdvancing()
    {
        var (session, blocks) = await Open();
        var current = _engine.ReviewStatus(session).CurrentBlockId!;
        var related = _engine.GetBlock(session, current).Block.RelatedBlockIds![0];

        _engine.GetBlock(session, related);

        Assert.Equal(current, _engine.ReviewStatus(session).CurrentBlockId);
    }

    [Fact] // R6 — One block at a time: review_status → get_block(current) shows exactly the current one
    public async Task R6_OneBlockAtATime()
    {
        var (session, _) = await Open();
        var status = _engine.ReviewStatus(session);
        var block = _engine.GetBlock(session, status.CurrentBlockId!).Block;

        Assert.Equal(status.CurrentBlockId, block.Id);
        Assert.Equal(1, status.Index);
        Assert.True(status.Total >= 1);
        Assert.Equal(status.CurrentBlockId, _engine.ReviewStatus(session).CurrentBlockId); // still not advanced
    }

    [Fact] // R7 — No verdict: the Block wire shape has no verdict/approval field at all (G-NOVERDICT)
    public void R7_NoVerdictField()
    {
        var forbidden = new[] { "verdict", "approved", "iscorrect", "correct", "safe", "score", "rating" };
        var properties = typeof(BlockView).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(ExplanationView).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name.ToLowerInvariant());

        Assert.DoesNotContain(properties, name => forbidden.Contains(name));
    }

    [Fact] // R8 — Per-block decision: next_block advances but NEVER auto-decides; status stays pending
    public async Task R8_AdvancingDoesNotDecide()
    {
        var (session, blocks) = await Open();
        var firstId = blocks[0].Id;

        _engine.NextBlock(session); // moved off the first block WITHOUT deciding it

        var status = _engine.ReviewStatus(session);
        Assert.Equal(0, status.Accepted);
        Assert.Equal(0, status.Corrections);
        // The skipped block is still decidable: recovery can return to it.
        Assert.NotNull(_engine.GetBlock(session, firstId).Block);
    }

    [Fact] // R9 — First tiny step: get_review_plan returns exactly ONE first_block (blocks[0])
    public async Task R9_FirstTinyStep()
    {
        var plan = await _engine.GetReviewPlanAsync(new Source.Local());
        Assert.Equal(plan.Blocks[0].Id, plan.FirstBlock.Id);
        Assert.False(string.IsNullOrWhiteSpace(plan.FirstBlock.Code)); // co-present, ready to read
    }

    [Fact] // R10 — Confirm before posting: submit_review without confirm never posts (posted:false)
    public async Task R10_ConfirmBeforePosting()
    {
        var plan = await _engine.GetReviewPlanAsync(new Source.Local());
        foreach (var b in plan.Blocks)
            _engine.AcceptBlock(plan.Session, b.Id);

        var preview = _engine.SubmitReview(plan.Session, confirm: false);
        Assert.False(preview.Posted); // Mode A posts nothing; Mode B would wait for explicit confirm
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
