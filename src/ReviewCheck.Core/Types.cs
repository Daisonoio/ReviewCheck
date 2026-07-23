namespace ReviewCheck.Core;

/// <summary>Intent category for a block (spec/mcp-tools.json → Block.intent).</summary>
public enum Intent
{
    Definition,
    Config,
    Wiring,
    Usage,
    Test,
    Refactor,
    Other,
}

/// <summary>Human decision status for a block (spec/session-state.schema.json → blocks[].status).</summary>
public enum BlockStatus
{
    Pending,
    Accepted,
    CorrectionRequested,
}

/// <summary>Kind of edge in the deterministic block graph (spec/session-state.schema.json → block_relations[].kind).</summary>
public enum RelationKind
{
    Uses,
    Defines,
    DependsOn,
    Tests,
    Configures,
}

/// <summary>
/// Review outcome — derived from the sum of human decisions, never decided by the AI
/// (spec/mcp-tools.json → submit_review.outcome).
/// </summary>
public enum ReviewOutcome
{
    ReadyToProceed,
    CorrectionsToApply,
    Approve,
    RequestChanges,
    CommentOnly,
}

/// <summary>A citation points to specific lines in the code (grounding). e.g. Lines = "12" or "12-15".</summary>
public sealed record Citation(string File, string Lines);

/// <summary>
/// An explanation that DESCRIBES AND ASKS, never a verdict. Anchored to the code via <see cref="Citations"/>.
/// The co-presence + grounding invariant is enforced by <see cref="BlockGuard"/>.
/// </summary>
public sealed record Explanation(
    string What,
    string Why,
    string Link,
    IReadOnlyList<Citation> Citations,
    string? Uncertainty = null);

/// <summary>
/// A unit of intent. Code and explanation are always co-present. The code is the source of truth;
/// the explanation is anchored to it (spec/mcp-tools.json → $defs.Block).
/// </summary>
public sealed record Block(
    string Id,
    string Title,
    Intent Intent,
    string Code,
    Explanation Explanation,
    IReadOnlyList<string>? RelatedBlockIds = null,
    int? EstimatedMinutes = null);

/// <summary>Position in the reading order (1-based index over the total).</summary>
public sealed record Position(int Index, int Total);

/// <summary>Interaction point (seam) — an attention pointer derived from the graph, not a verdict.</summary>
public sealed record InteractionPoint(string Text, IReadOnlyList<string> BlockIds);

/// <summary>Relationship between two blocks, from the deterministic dependency graph.</summary>
public sealed record BlockRelation(string From, string To, RelationKind Kind);

/// <summary>A requested correction note (a to-do item in Mode A; a comment in Mode B).</summary>
public sealed record CorrectionNote(string BlockId, string Note);

/// <summary>What is under review. Mode A = local diff (primary); Mode B = a platform PR.</summary>
public abstract record Source
{
    private Source() { }

    /// <summary>Mode A: the local git diff. Ref: 'working' (default) | 'staged' | a git range | a commit.</summary>
    public sealed record Local(string? Ref = "working") : Source;

    /// <summary>Mode B: a PR on a platform, read/posted with the user's local token.</summary>
    public sealed record PullRequest(string Platform, string Repo, string Pr) : Source;
}
