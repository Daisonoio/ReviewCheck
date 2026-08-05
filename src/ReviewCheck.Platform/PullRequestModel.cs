namespace ReviewCheck.Platform;

/// <summary>
/// One open pull request, as listed for the "choose which PR to review" step. <see cref="IsSelfReview"/>
/// is computed once here (author == the authenticated token identity) so a caller can tell the user
/// BEFORE they open the review that approval won't be available (GUARDRAILS G10) — not as a surprise
/// after GitHub rejects the call.
/// </summary>
public sealed record PullRequestSummary(string Number, string Title, string Author, bool IsSelfReview);

/// <summary>
/// A single review comment anchored to a line — built ONLY from the reviewer's own
/// <c>request_correction</c> note (GUARDRAILS G9): the LLM's <c>what</c>/<c>why</c> narration never
/// reaches this type.
/// </summary>
public sealed record PullRequestComment(string Path, string Line, string Body);

/// <summary>
/// The formal outcome of a submitted review. <c>Approve</c>/<c>RequestChanges</c> are a platform
/// VERDICT event — reachable only through <c>submit_review(confirm:true)</c> (GUARDRAILS G8), and
/// never offered at all when <see cref="PullRequestSummary.IsSelfReview"/> is true (GUARDRAILS G10).
/// </summary>
public enum PullRequestReviewEvent
{
    Comment,
    Approve,
    RequestChanges,
}
