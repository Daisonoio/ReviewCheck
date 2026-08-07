namespace ReviewCheck.Platform;

/// <summary>
/// The seam for reviewing a REMOTE pull request instead of a local diff (docs plan, PR-review phase).
/// One interface, GitHub first (<see cref="GitHubPullRequestPlatform"/>); the same shape covers other
/// platforms later (Azure DevOps is already reserved in <c>Source.PullRequest.Platform</c>).
///
/// There is no per-block "post one comment now" method. Every comment collected during a review is
/// batched into ONE <see cref="SubmitReviewAsync"/> call, made only when the caller has an explicit
/// <c>confirm:true</c> in hand (GUARDRAILS G8) — nothing in this interface lets a caller post
/// incrementally, so "post before the human confirms" is not a discipline to remember, it is
/// impossible to express against this seam.
/// </summary>
public interface IPullRequestPlatform
{
    /// <summary>Open pull requests for <paramref name="repo"/> ("owner/name"), each flagged for self-review.</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListAsync(string repo, CancellationToken ct = default);

    /// <summary>
    /// One pull request's summary, flagged for self-review — same shape <see cref="ListAsync"/> already
    /// computes per item, for the case where the caller opens a PR directly by number (GUARDRAILS G10)
    /// instead of picking it from the list.
    /// </summary>
    Task<PullRequestSummary> GetSummaryAsync(string repo, string pr, CancellationToken ct = default);

    /// <summary>The pull request's diff as unified-diff text — parsed downstream by <see cref="UnifiedDiffParser"/>, unchanged.</summary>
    Task<string> GetDiffAsync(string repo, string pr, CancellationToken ct = default);

    /// <summary>
    /// The commit SHA the pull request's diff is relative to — needed alongside the diff itself
    /// because <see cref="GetDiffAsync"/> can only return diff text OR PR metadata per call (they're
    /// different response media types for the same GitHub endpoint), never both at once.
    /// </summary>
    Task<string> GetHeadRefAsync(string repo, string pr, CancellationToken ct = default);

    /// <summary>
    /// A file's full content at <paramref name="ref"/> — the pipeline (Roslyn) needs the WHOLE file,
    /// not just the changed hunks, same reason <c>LocalDiffReader</c> loads <c>NewText</c> for local
    /// diffs. Null (not a throw) when unreadable — a missing file degrades that block gracefully
    /// (P8), it does not fail the review.
    /// </summary>
    Task<string?> GetFileContentAsync(string repo, string @ref, string path, CancellationToken ct = default);

    /// <summary>The identity the configured token authenticates as — the other half of the self-review check.</summary>
    Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default);

    /// <summary>
    /// The one and only posting call. <paramref name="reviewEvent"/> must never be
    /// <see cref="PullRequestReviewEvent.Approve"/> or <see cref="PullRequestReviewEvent.RequestChanges"/>
    /// for a self-review — that is the CALLER's responsibility (GUARDRAILS G10); this method does not
    /// re-derive it, so it cannot silently "fix" a caller that gets it wrong.
    /// </summary>
    Task SubmitReviewAsync(
        string repo, string pr, PullRequestReviewEvent reviewEvent,
        IReadOnlyList<PullRequestComment> comments, CancellationToken ct = default);
}

/// <summary>
/// The platform cannot be reached, or the token is missing/rejected. Deliberately one exception type
/// (mirrors <c>ReviewCheck.Llm.LlmUnavailableException</c>): callers react the same way regardless of
/// cause. Messages must never contain the token.
/// </summary>
public sealed class PullRequestPlatformUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
