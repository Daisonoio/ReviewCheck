using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Platform;
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

        var result = await engine.SubmitReviewAsync(plan.Session, confirm: true);

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

        var result = await engine.SubmitReviewAsync(plan.Session, confirm: false);

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

        var result = await engine.SubmitReviewAsync(plan.Session, confirm: false);

        Assert.Equal("corrections_to_apply", result.Outcome);
        Assert.False(result.Posted);
        Assert.NotNull(result.Notes);
        Assert.Contains(result.Notes!, n => n.Note == "extract a constant");
    }

    [Fact]
    public async Task Submit_PullRequestSource_NoPlatformConfigured_FailsClosed_PostsNothing()
    {
        var engine = NewEngine();
        // Open a PR-source session directly through the store, with no pullRequestPlatform wired into
        // the engine — is_self_review is also unset (null), so this exercises BOTH fail-closed paths.
        var store = new SessionStore(_root);
        var analyzed = await new StubProvider().AnalyzeAsync(new Source.Local(), new FactsNarrator());
        var session = store.Create(analyzed, new Source.PullRequest("github", "org/repo", "42"));
        foreach (var b in analyzed.Blocks)
            store.SetStatus(session, b.Id, BlockStatus.Accepted);

        var result = await engine.SubmitReviewAsync(session, confirm: true);

        Assert.Equal("comment_only", result.Outcome);
        Assert.False(result.Posted);
    }

    // ---- submit_review on a PR: real posting, gated by confirm and is_self_review (T4, GUARDRAILS G8/G10) ----

    private static async Task<string> OpenPrSession(
        SessionStore store, IPullRequestPlatform platform, string prNumber, bool allAccepted, string? correctionNote = null)
    {
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);
        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", prNumber));
        foreach (var b in plan.Blocks)
            engine.AcceptBlock(plan.Session, b.Id);
        if (correctionNote is not null)
            engine.RequestCorrection(plan.Session, plan.Blocks[0].Id, correctionNote);
        return plan.Session;
    }

    [Fact]
    public async Task Submit_OthersPr_AllAccepted_Confirmed_PostsApprove()
    {
        var store = new SessionStore(_root);
        var platform = new FakePullRequestPlatform([new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)]);
        var session = await OpenPrSession(store, platform, "7", allAccepted: true);
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);

        var result = await engine.SubmitReviewAsync(session, confirm: true);

        Assert.Equal("approve", result.Outcome);
        Assert.True(result.Posted);
        Assert.Equal(PullRequestReviewEvent.Approve, platform.LastSubmit!.Value.Event);
        Assert.Empty(platform.LastSubmit.Value.Comments);
    }

    [Fact]
    public async Task Submit_OthersPr_WithCorrection_Confirmed_PostsRequestChangesWithComment()
    {
        var store = new SessionStore(_root);
        var platform = new FakePullRequestPlatform([new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)]);
        var session = await OpenPrSession(store, platform, "7", allAccepted: true, correctionNote: "please add a null check");
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);

        var result = await engine.SubmitReviewAsync(session, confirm: true);

        Assert.Equal("request_changes", result.Outcome);
        Assert.True(result.Posted);
        Assert.Equal(PullRequestReviewEvent.RequestChanges, platform.LastSubmit!.Value.Event);
        Assert.Contains(platform.LastSubmit.Value.Comments, c => c.Body == "please add a null check");
    }

    [Fact]
    public async Task Submit_OwnPr_AllAccepted_Confirmed_ForcesCommentOnly_NeverApprove()
    {
        var store = new SessionStore(_root);
        var platform = new FakePullRequestPlatform([new PullRequestSummary("2", "Mine", "alice", IsSelfReview: true)]);
        var session = await OpenPrSession(store, platform, "2", allAccepted: true);
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);

        var result = await engine.SubmitReviewAsync(session, confirm: true);

        Assert.Equal("comment_only", result.Outcome);
        Assert.True(result.Posted);
        Assert.Equal(PullRequestReviewEvent.Comment, platform.LastSubmit!.Value.Event);
    }

    [Fact]
    public async Task Submit_Pr_WithoutConfirm_PreviewsOutcome_AndCallsThePlatformNever()
    {
        var store = new SessionStore(_root);
        var platform = new FakePullRequestPlatform([new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)]);
        var session = await OpenPrSession(store, platform, "7", allAccepted: true);
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);

        var result = await engine.SubmitReviewAsync(session, confirm: false);

        Assert.Equal("approve", result.Outcome);
        Assert.False(result.Posted);
        Assert.Null(platform.LastSubmit); // G6/G8: no confirm, no network call at all
    }

    [Fact]
    public async Task Submit_Pr_PlatformThrowsOnPosting_ReturnsUnpostedWithReason()
    {
        var store = new SessionStore(_root);
        var summaryPlatform = new FakePullRequestPlatform([new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)]);
        var session = await OpenPrSession(store, summaryPlatform, "7", allAccepted: true);
        var throwingPlatform = new FakePullRequestPlatform(
            [new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)], throwOnSubmit: true);
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: throwingPlatform);

        var result = await engine.SubmitReviewAsync(session, confirm: true);

        Assert.Equal("approve", result.Outcome);
        Assert.False(result.Posted);
        Assert.Contains("scripted posting failure", result.Summary);
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

    // ---- list_pull_requests: excludes self-authored PRs (GUARDRAILS G10) ----

    private sealed class FakePullRequestPlatform(
        IReadOnlyList<PullRequestSummary> summaries, bool throwOnSummary = false, bool throwOnSubmit = false)
        : IPullRequestPlatform
    {
        public (string Repo, string Pr, PullRequestReviewEvent Event, IReadOnlyList<PullRequestComment> Comments, string? Body)? LastSubmit
        { get; private set; }

        public Task<IReadOnlyList<PullRequestSummary>> ListAsync(string repo, CancellationToken ct = default) =>
            Task.FromResult(summaries);

        public Task<PullRequestSummary> GetSummaryAsync(string repo, string pr, CancellationToken ct = default)
        {
            if (throwOnSummary)
                throw new PullRequestPlatformUnavailableException("scripted failure");
            var match = summaries.SingleOrDefault(s => s.Number == pr)
                        ?? throw new PullRequestPlatformUnavailableException($"no fixture PR '{pr}'");
            return Task.FromResult(match);
        }

        public Task<string> GetDiffAsync(string repo, string pr, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised here.");
        public Task<string> GetHeadRefAsync(string repo, string pr, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised here.");
        public Task<string?> GetFileContentAsync(string repo, string @ref, string path, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised here.");
        public Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised here.");

        public Task SubmitReviewAsync(string repo, string pr, PullRequestReviewEvent reviewEvent,
            IReadOnlyList<PullRequestComment> comments, string? body = null, CancellationToken ct = default)
        {
            if (throwOnSubmit)
                throw new PullRequestPlatformUnavailableException("scripted posting failure");
            LastSubmit = (repo, pr, reviewEvent, comments, body);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ListOtherPullRequests_ExcludesSelfAuthored()
    {
        var platform = new FakePullRequestPlatform([
            new PullRequestSummary("1", "Someone else's PR", "bob", IsSelfReview: false),
            new PullRequestSummary("2", "My own PR", "alice", IsSelfReview: true),
        ]);
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root), pullRequestPlatform: platform);

        var others = await engine.ListOtherPullRequestsAsync("github", "org/repo");

        Assert.Single(others);
        Assert.Equal("1", others[0].Number);
    }

    [Fact]
    public async Task ListOtherPullRequests_UnsupportedPlatform_ThrowsClearly()
    {
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root),
            pullRequestPlatform: new FakePullRequestPlatform([]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ListOtherPullRequestsAsync("azure_devops", "org/repo"));

        Assert.Contains("azure_devops", ex.Message);
    }

    [Fact]
    public async Task ListOtherPullRequests_NoPlatformConfigured_ThrowsClearly()
    {
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ListOtherPullRequestsAsync("github", "org/repo"));
    }

    // ---- get_review_plan: is_self_review, computed once, up front (T3, GUARDRAILS G10) ----

    [Fact]
    public async Task GetReviewPlan_LocalSource_IsSelfReviewIsNull_NotApplicable()
    {
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root));

        var plan = await engine.GetReviewPlanAsync(new Source.Local("working"));

        Assert.Null(plan.IsSelfReview);
    }

    [Fact]
    public async Task GetReviewPlan_PrOpenedByAuthenticatedUser_IsSelfReviewTrue()
    {
        var platform = new FakePullRequestPlatform([new PullRequestSummary("2", "Mine", "alice", IsSelfReview: true)]);
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root), pullRequestPlatform: platform);

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "2"));

        Assert.True(plan.IsSelfReview);
    }

    [Fact]
    public async Task GetReviewPlan_PrOpenedByAuthenticatedUser_PersistsInSessionState()
    {
        var platform = new FakePullRequestPlatform([new PullRequestSummary("2", "Mine", "alice", IsSelfReview: true)]);
        var store = new SessionStore(_root);
        var engine = new ReviewEngine(new StubProvider(), store, pullRequestPlatform: platform);

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "2"));
        var reloaded = store.Load(plan.Session);

        Assert.True(reloaded.Source.IsSelfReview);
    }

    [Fact]
    public async Task GetReviewPlan_PrOpenedByAnotherUser_IsSelfReviewFalse()
    {
        var platform = new FakePullRequestPlatform([new PullRequestSummary("7", "Not mine", "bob", IsSelfReview: false)]);
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root), pullRequestPlatform: platform);

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "7"));

        Assert.False(plan.IsSelfReview);
    }

    [Fact]
    public async Task GetReviewPlan_PlatformThrows_FailsClosed_IsSelfReviewTrue()
    {
        var platform = new FakePullRequestPlatform([], throwOnSummary: true);
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root), pullRequestPlatform: platform);

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "2"));

        Assert.True(plan.IsSelfReview);
    }

    [Fact]
    public async Task GetReviewPlan_NoPlatformConfigured_FailsClosed_IsSelfReviewTrue()
    {
        // No pullRequestPlatform injected at all -- still a PR source, so it must fail closed, not null.
        var engine = new ReviewEngine(new StubProvider(), new SessionStore(_root));

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "2"));

        Assert.True(plan.IsSelfReview);
    }
}
