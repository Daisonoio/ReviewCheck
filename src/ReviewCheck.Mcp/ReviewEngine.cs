using ModelContextProtocol.Server;
using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp;

/// <summary>
/// The deterministic core of the 8 tools (docs/23 §2), independent of the MCP transport
/// so it can be unit-tested directly against the stub. Two invariants are enforced here,
/// not merely instructed:
/// <list type="bullet">
///   <item>every returned block passes <see cref="BlockGuard.Ensure"/> (co-presence + grounding);</item>
///   <item><c>get_block</c> never moves the pointer — only <c>next_block</c> does (recovery R5/R6).</item>
/// </list>
/// The outcome is only ever the sum of the human decisions — there is no verdict path (G-NOVERDICT).
/// </summary>
public sealed class ReviewEngine(IReviewProvider provider, SessionStore store, NarratorResolver? narrators = null)
{
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

        var session = store.Create(analyzed, source);

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

        return new ReviewPlanResult(session, analyzed.Title, analyzed.EstimatedMinutes, summaries, seams, BlockView.From(first), notice);
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
    /// </summary>
    public SubmitResult SubmitReview(string session, bool confirm)
    {
        var state = store.Load(session);

        var undecided = state.Blocks
            .Where(b => b.Status == BlockStatus.Pending)
            .Select(b => b.Id)
            .ToList();

        var notes = state.Blocks
            .Where(b => b.Status == BlockStatus.CorrectionRequested)
            .Select(b => new NoteView(b.Id, b.Note ?? string.Empty))
            .ToList();
        var noteList = notes.Count > 0 ? notes : null;

        // G6: incomplete review never closes. "ready_to_proceed" is emitted only when every block is accepted.
        if (undecided.Count > 0)
            return new SubmitResult(
                Outcome: "corrections_to_apply",
                Posted: false,
                Summary: $"Review not complete: {undecided.Count} block(s) still undecided. Decide each before closing.",
                Notes: noteList,
                UndecidedBlocks: undecided);

        // Reviewing a pull request is not part of the MVP: no token, no network, nothing posted.
        if (state.Source.Type == "pull_request")
            return new SubmitResult(
                Outcome: "comment_only",
                Posted: false,
                Summary: "Reviewing a pull request is not included in this MVP. Nothing was posted.",
                Notes: noteList,
                UndecidedBlocks: null);

        // Local review: present the outcome; post nothing (the self-approval problem does not exist).
        return notes.Count > 0
            ? new SubmitResult("corrections_to_apply", false,
                $"{notes.Count} correction(s) to apply; the rest is accepted.", noteList, null)
            : new SubmitResult("ready_to_proceed", false,
                "All blocks accepted — ready to proceed.", null, null);
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
