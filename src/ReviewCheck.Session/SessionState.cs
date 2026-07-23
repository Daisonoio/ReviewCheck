using System.Text.Json;
using ReviewCheck.Core;

namespace ReviewCheck.Session;

/// <summary>
/// The local state file (spec/session-state.schema.json). No database, no network:
/// this is the whole persistence layer. Reuses the <c>ReviewCheck.Core</c> records
/// (<see cref="Explanation"/>, <see cref="Citation"/>, <see cref="InteractionPoint"/>,
/// <see cref="BlockRelation"/>) so a change to the domain shape breaks this by construction.
/// Field names map to the schema's snake_case via <see cref="SessionJson.Options"/>.
/// </summary>
public sealed record SessionState(
    string Session,
    SourceState Source,
    string Title,
    IReadOnlyList<BlockState> Blocks,
    ProgressState Progress)
{
    public string? HeadSha { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public IReadOnlyList<BlockRelation>? BlockRelations { get; init; }
    public IReadOnlyList<InteractionPoint>? InteractionPoints { get; init; }
}

/// <summary>What is under review. Mode A = local diff (primary); Mode B = a platform PR.</summary>
public sealed record SourceState(
    string Type,
    string? Ref = null,
    string? Platform = null,
    string? Repo = null,
    string? Pr = null)
{
    public static SourceState FromSource(Source source) => source switch
    {
        Source.Local local => new SourceState("local", local.Ref),
        Source.PullRequest pr => new SourceState("pull_request", null, pr.Platform, pr.Repo, pr.Pr),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source kind."),
    };

    public Source ToSource() => Type switch
    {
        "local" => new Source.Local(Ref),
        "pull_request" => new Source.PullRequest(Platform!, Repo!, Pr!),
        _ => throw new InvalidOperationException($"Unknown source type '{Type}'."),
    };
}

/// <summary>
/// A block plus the session-level facts the schema adds on top of a domain <see cref="Block"/>:
/// its 0-based reading position, the human decision, and the correction note.
/// </summary>
public sealed record BlockState(
    string Id,
    int OrderIndex,
    string Title,
    Intent Intent,
    string Code,
    Explanation Explanation,
    BlockStatus Status,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? RelatedBlockIds = null,
    int? EstimatedMinutes = null,
    string? Note = null)
{
    /// <summary>Rebuilds the domain block (what the MCP tools return co-present).</summary>
    public Block ToBlock() => new(Id, Title, Intent, Code, Explanation, RelatedBlockIds, EstimatedMinutes);

    public static BlockState FromBlock(int orderIndex, Block block, BlockStatus status = BlockStatus.Pending, string? note = null) =>
        new(block.Id, orderIndex, block.Title, block.Intent, block.Code, block.Explanation, status,
            Files: null, RelatedBlockIds: block.RelatedBlockIds, EstimatedMinutes: block.EstimatedMinutes, Note: note);
}

/// <summary>The resume point (F7): where the reader is and what they have navigated past.</summary>
public sealed record ProgressState(
    string? CurrentBlockId,
    IReadOnlyList<string> CompletedBlockIds,
    JsonElement? ResumedContext = null);
