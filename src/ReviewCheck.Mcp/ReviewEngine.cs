using ModelContextProtocol.Server;
using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Platform;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp;

/// <summary>
/// The deterministic core of the tools (docs/23 §2), independent of the MCP transport
/// so it can be unit-tested directly against the stub. Two invariants are enforced here,
/// not merely instructed:
/// <list type="bullet">
///   <item>every returned block passes <see cref="BlockGuard.Ensure"/> (co-presence + grounding);</item>
///   <item><c>get_block</c> never moves the pointer — only <c>next_block</c> does (recovery R5/R6).</item>
/// </list>
/// The outcome is only ever the sum of the human decisions — there is no verdict path (G-NOVERDICT).
/// </summary>
public sealed class ReviewEngine(
    IReviewProvider provider, SessionStore store,
    NarratorResolver? narrators = null, IPullRequestPlatform? pullRequestPlatform = null)
{
    /// <summary>
    /// Open pull requests for <paramref name="repo"/>, EXCLUDING ones the authenticated user opened
    /// themselves (GUARDRAILS G10 — you can't approve your own PR, so it's not offered as something
    /// to pick from this list). No session is opened; this is a pure lookup for the "which PR?" step
    /// before <c>get_review_plan</c>.
    /// </summary>
    public async Task<IReadOnlyList<PullRequestSummary>> ListOtherPullRequestsAsync(string platform, string repo)
    {
        if (!string.Equals(platform, "github", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Platform '{platform}' is not supported yet — only 'github' lists pull requests today.");
        if (pullRequestPlatform is null)
            throw new InvalidOperationException("No pull request platform is configured.");

        var all = await pullRequestPlatform.ListAsync(repo);
        return all.Where(p => !p.IsSelfReview).ToList();
    }

    /// <summary>
    /// Analyzes the source, opens a session, and returns the plan + the FIRST co-present block.
    /// The narrator (key LLM / host sampling / facts) is chosen here from the host's capabilities,
    /// and a matching <c>notice</c> is surfaced so the agent can show the mode banner.
    /// </summary>
    public async Task<ReviewPlanResult> GetReviewPlanAsync(Source source, McpServer? server = null)
    {
        var (narrator, notice) = await (narrators ?? NarratorResolver.FactsOnly).ResolveAsync(server);

        var analyzed = await provider.AnalyzeAsync(source, narrator);
        if (analyzed.Blocks.Count == 0)
            throw new InvalidOperationException("The analysis produced no blocks.");

        var isSelfReview = await ResolveIsSelfReviewAsync(source);
        var session = store.Create(analyzed, source, isSelfReview);

        var summaries = analyzed.Blocks
            .Select((b, i) => new BlockSummaryView(b.Id, i, b.Title, Wire.Intent(b.Intent), b.EstimatedMinutes))
            .ToList();

        var seams = analyzed.InteractionPoints
            .Select(p => new InteractionPointView(p.Text, p.BlockIds))
            .ToList();

        // The mode notice is honesty-critical (it tells the user how the explanations were produced),
        // so it must not depend on the host choosing to render a plan-level banner. Fold it into the
        // FIRST block's uncertainty — which rides the co-presence guarantee and is always shown —
        // so the disclosure is guaranteed, not merely instructed. It appears once, on the first block.
        var first = BlockGuard.Ensure(analyzed.Blocks[0]);
        if (!string.IsNullOrWhiteSpace(notice))
            first = first with
            {
                Explanation = first.Explanation with
                {
                    Uncertainty = string.IsNullOrWhiteSpace(first.Explanation.Uncertainty)
                        ? notice
                        : $"{notice}\n\n{first.Explanation.Uncertainty}"
                }
            };

        return new ReviewPlanResult(session, analyzed.Title, analyzed.EstimatedMinutes, summaries, seams, BlockView.From(first), notice, isSelfReview);
    }

    /// <summary>
    /// GUARDRAILS G10: computed ONCE, here, whether opening a PR review or picking one from
    /// <see cref="ListOtherPullRequestsAsync"/> — this is the path that catches the case where the
    /// caller opens a PR directly by number, bypassing the list (where it's already excluded).
    /// Fail-CLOSED: if it can't be determined (platform unconfigured, network hiccup), the result is
    /// <c>true</c> — treated as a self-review — because the failure mode of wrongly WITHHOLDING
    /// approve/request_changes is a false "you can't approve this", not a missed self-approval.
    /// </summary>
    private async Task<bool?> ResolveIsSelfReviewAsync(Source source)
    {
        if (source is not Source.PullRequest { Platform: "github" } pr)
            return null; // not a PR at all — the question doesn't apply, distinct from "couldn't tell"

        if (pullRequestPlatform is null)
            return true; // a PR, but nothing to ask — fail closed, same as a thrown exception below

        try
        {
            var summary = await pullRequestPlatform.GetSummaryAsync(pr.Repo, pr.Pr);
            return summary.IsSelfReview;
        }
        catch (PullRequestPlatformUnavailableException)
        {
            return true;
        }
    }

    /// <summary>Advances the pointer and returns the next block co-present, with position and progress.</summary>
    public NextBlockResult NextBlock(string session)
    {
        var (block, position) = store.Advance(session);
        BlockGuard.Ensure(block);
        var state = store.Load(session);
        return new NextBlockResult(BlockView.From(block), new PositionView(position.Index, position.Total), ProgressPct(state));
    }

    /// <summary>Returns a block co-present WITHOUT advancing (peek/recovery). Backs R1–R5.</summary>
    public GetBlockResult GetBlock(string session, string blockId)
    {
        var state = store.Load(session);
        var stored = state.Blocks.FirstOrDefault(b => b.Id == blockId)
            ?? throw new BlockNotFoundException(session, blockId);
        var block = BlockGuard.Ensure(stored.ToBlock());
        return new GetBlockResult(BlockView.From(block));
    }

    public AcceptResult AcceptBlock(string session, string blockId)
    {
        store.SetStatus(session, blockId, BlockStatus.Accepted);
        return new AcceptResult(true, "accepted");
    }

    public CorrectionResult RequestCorrection(string session, string blockId, string note)
    {
        store.SetStatus(session, blockId, BlockStatus.CorrectionRequested, note);
        return new CorrectionResult(true, "correction_requested", "n-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public ReviewStatusResult ReviewStatus(string session)
    {
        var state = store.Load(session);
        var accepted = state.Blocks.Count(b => b.Status == BlockStatus.Accepted);
        var corrections = state.Blocks.Count(b => b.Status == BlockStatus.CorrectionRequested);
        return new ReviewStatusResult(
            Index: IndexOfCurrent(state),
            Total: state.Blocks.Count,
            ProgressPct: ProgressPct(state),
            CurrentBlockId: state.Progress.CurrentBlockId,
            Accepted: accepted,
            Corrections: corrections);
    }

    /// <summary>
    /// Closes the review. The outcome is the sum of the human decisions; nothing is ever a verdict.
    /// Deterministic gate G6: with any undecided block it does not close (returns undecided_blocks).
    /// Local review: nothing is ever posted — no token, no network.
    /// Pull request: posts ONE batched review via <see cref="IPullRequestPlatform.SubmitReviewAsync"/>,
    /// and only when <paramref name="confirm"/> is true (G6/G8) — without it, this returns a preview of
    /// what WOULD be posted and makes no network call. GUARDRAILS G10: when the session's
    /// <c>is_self_review</c> is true (or unresolved — fails closed), the review event is forced to
    /// <see cref="PullRequestReviewEvent.Comment"/>; <c>Approve</c>/<c>RequestChanges</c> are never even
    /// constructed for a self-review, let alone sent.
    /// </summary>
    public async Task<SubmitResult> SubmitReviewAsync(string session, bool confirm)
    {
        var state = store.Load(session);

        var undecided = state.Blocks
            .Where(b => b.Status == BlockStatus.Pending)
            .Select(b => b.Id)
            .ToList();

        var correctionBlocks = state.Blocks
            .Where(b => b.Status == BlockStatus.CorrectionRequested)
            .ToList();
        var notes = correctionBlocks.Select(b => new NoteView(b.Id, b.Note ?? string.Empty)).ToList();
        var noteList = notes.Count > 0 ? notes : null;

        // G6: incomplete review never closes. "ready_to_proceed" is emitted only when every block is accepted.
        if (undecided.Count > 0)
            return new SubmitResult(
                Outcome: "corrections_to_apply",
                Posted: false,
                Summary: $"Review not complete: {undecided.Count} block(s) still undecided. Decide each before closing.",
                Notes: noteList,
                UndecidedBlocks: undecided);

        if (state.Source.Type == "pull_request")
            return await SubmitPullRequestReviewAsync(state, correctionBlocks, noteList, confirm);

        // Local review: present the outcome; post nothing (the self-approval problem does not exist).
        return notes.Count > 0
            ? new SubmitResult("corrections_to_apply", false,
                $"{notes.Count} correction(s) to apply; the rest is accepted.", noteList, null)
            : new SubmitResult("ready_to_proceed", false,
                "All blocks accepted — ready to proceed.", null, null);
    }

    private async Task<SubmitResult> SubmitPullRequestReviewAsync(
        SessionState state, IReadOnlyList<BlockState> correctionBlocks, IReadOnlyList<NoteView>? noteList, bool confirm)
    {
        // Fail-closed (G10): a missing flag (e.g. a session opened before this field existed) is
        // treated the same as "yes, this is a self-review" — never as license to approve.
        var isSelfReview = state.Source.IsSelfReview ?? true;
        var hasCorrections = correctionBlocks.Count > 0;

        var (outcome, reviewEvent) = (isSelfReview, hasCorrections) switch
        {
            (true, _) => ("comment_only", PullRequestReviewEvent.Comment),
            (false, true) => ("request_changes", PullRequestReviewEvent.RequestChanges),
            (false, false) => ("approve", PullRequestReviewEvent.Approve),
        };

        if (!confirm)
            return new SubmitResult(outcome, false,
                $"Preview only — nothing posted yet. Call again with confirm:true to post '{outcome}' to the pull request.",
                noteList, null);

        if (pullRequestPlatform is null)
            return new SubmitResult(outcome, false,
                "No pull request platform is configured — nothing was posted.", noteList, null);

        var pr = (Source.PullRequest)state.Source.ToSource();
        var comments = correctionBlocks
            .Select(b => new PullRequestComment(
                b.Explanation.Citations[0].File, b.Explanation.Citations[0].Lines, b.Note ?? string.Empty))
            .ToList();
        var body = isSelfReview
            ? "Reviewed with ReviewCheck. This is the reviewer's own pull request, so it closes as comment-only — never an approval or a request for changes (GUARDRAILS G10)."
            : hasCorrections
                ? $"Reviewed with ReviewCheck. {correctionBlocks.Count} correction(s) requested — see inline comments."
                : "Reviewed with ReviewCheck. All blocks accepted.";

        try
        {
            await pullRequestPlatform.SubmitReviewAsync(pr.Repo, pr.Pr, reviewEvent, comments, body);
        }
        catch (PullRequestPlatformUnavailableException e)
        {
            return new SubmitResult(outcome, false,
                $"Could not post the review to the pull request: {e.Message}", noteList, null);
        }

        return new SubmitResult(outcome, true,
            $"Posted '{outcome}' to the pull request.", noteList, null);
    }

    /// <summary>
    /// Local oversight signals (GUARDRAILS.md §4), computed on request from THIS session's state
    /// only: grounding coverage (G2), forbidden evaluative language (G4), and the
    /// correction/acceptance ratio. Read-only — no side effects, no network.
    /// Reuses <see cref="ExplanationRubric.CountVerdictLanguage"/> so "evaluative language" is the
    /// exact rule the rubric already rejects, not a second, drifting definition of it.
    /// </summary>
    public ReviewHealthResult ReviewHealth(string session)
    {
        var state = store.Load(session);
        var total = state.Blocks.Count;

        var ungroundedCount = state.Blocks.Count(b => b.Explanation.Citations.Count == 0);

        var flagged = state.Blocks
            .Where(b => ExplanationRubric.CountVerdictLanguage(AssertiveText(b.Explanation)) > 0)
            .Select(b => b.Id)
            .ToList();
        var hits = state.Blocks.Sum(b => ExplanationRubric.CountVerdictLanguage(AssertiveText(b.Explanation)));

        var accepted = state.Blocks.Count(b => b.Status == BlockStatus.Accepted);
        var corrections = state.Blocks.Count(b => b.Status == BlockStatus.CorrectionRequested);
        var pending = state.Blocks.Count(b => b.Status == BlockStatus.Pending);
        var decided = accepted + corrections;

        return new ReviewHealthResult(
            TotalBlocks: total,
            Accepted: accepted,
            Corrections: corrections,
            Pending: pending,
            UngroundedBlocks: ungroundedCount,
            UngroundedPct: total == 0 ? 0 : Math.Round(ungroundedCount * 100.0 / total, 1),
            EvaluativeLanguageHits: hits,
            FlaggedBlockIds: flagged,
            CorrectionRatePct: decided == 0 ? null : Math.Round(corrections * 100.0 / decided, 1),
            Note: "Signals about how this review was built and decided — not a verdict on the code or a measure of the reviewer.");
    }

    private static string AssertiveText(Explanation e) => $"{e.What}\n{e.Why}\n{e.Link}";

    private static double ProgressPct(SessionState state)
    {
        var total = state.Blocks.Count;
        if (total == 0)
            return 0;
        var decided = state.Blocks.Count(b => b.Status != BlockStatus.Pending);
        return Math.Round(decided * 100.0 / total, 1);
    }

    private static int IndexOfCurrent(SessionState state)
    {
        for (var i = 0; i < state.Blocks.Count; i++)
            if (state.Blocks[i].Id == state.Progress.CurrentBlockId)
                return i + 1;
        return 0;
    }
}
