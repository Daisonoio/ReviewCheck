using System.Text.Json.Serialization;
using ReviewCheck.Core;

namespace ReviewCheck.Mcp;

// Wire shapes for the MCP tool outputs. Field names are the snake_case of
// spec/mcp-tools.json (outputSchema), pinned with [JsonPropertyName] so the
// contract holds regardless of the serializer's default naming policy.
// The domain types stay clean; mapping happens here.

/// <summary>The lines an explanation is anchored to (grounding).</summary>
public sealed record CitationView(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("lines")] string Lines);

/// <summary>An explanation that describes and asks, anchored to citations.</summary>
public sealed record ExplanationView(
    [property: JsonPropertyName("what")] string What,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("link")] string Link,
    [property: JsonPropertyName("citations")] IReadOnlyList<CitationView> Citations,
    [property: JsonPropertyName("uncertainty")] string? Uncertainty);

/// <summary>A block rendered co-present (code + explanation together).</summary>
public sealed record BlockView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("explanation")] ExplanationView Explanation,
    [property: JsonPropertyName("related_block_ids")] IReadOnlyList<string>? RelatedBlockIds,
    [property: JsonPropertyName("estimated_minutes")] int? EstimatedMinutes)
{
    public static BlockView From(Block block) => new(
        block.Id,
        block.Title,
        Wire.Intent(block.Intent),
        block.Code,
        new ExplanationView(
            block.Explanation.What,
            block.Explanation.Why,
            block.Explanation.Link,
            block.Explanation.Citations.Select(c => new CitationView(c.File, c.Lines)).ToList(),
            block.Explanation.Uncertainty),
        block.RelatedBlockIds,
        block.EstimatedMinutes);
}

/// <summary>A block summary in the plan (no code — the plan is the map, not the territory).</summary>
public sealed record BlockSummaryView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("order_index")] int OrderIndex,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("estimated_minutes")] int? EstimatedMinutes);

/// <summary>A seam to verify (attention pointer, not a verdict).</summary>
public sealed record InteractionPointView(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("block_ids")] IReadOnlyList<string> BlockIds);

/// <summary>Position in the reading order (1-based).</summary>
public sealed record PositionView(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total);

/// <summary>A correction note (Mode A: a to-do; Mode B: a comment).</summary>
public sealed record NoteView(
    [property: JsonPropertyName("block_id")] string BlockId,
    [property: JsonPropertyName("note")] string Note);

// --- Tool results (one per tool, matching outputSchema) ---

public sealed record ReviewPlanResult(
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("estimated_minutes")] int? EstimatedMinutes,
    [property: JsonPropertyName("blocks")] IReadOnlyList<BlockSummaryView> Blocks,
    [property: JsonPropertyName("interaction_points")] IReadOnlyList<InteractionPointView> InteractionPoints,
    [property: JsonPropertyName("first_block")] BlockView FirstBlock);

public sealed record NextBlockResult(
    [property: JsonPropertyName("block")] BlockView Block,
    [property: JsonPropertyName("position")] PositionView Position,
    [property: JsonPropertyName("progress_pct")] double ProgressPct);

public sealed record GetBlockResult(
    [property: JsonPropertyName("block")] BlockView Block);

public sealed record AcceptResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("block_status")] string BlockStatus);

public sealed record CorrectionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("block_status")] string BlockStatus,
    [property: JsonPropertyName("note_id")] string NoteId);

public sealed record ReviewStatusResult(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("progress_pct")] double ProgressPct,
    [property: JsonPropertyName("current_block_id")] string? CurrentBlockId,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("corrections")] int Corrections);

public sealed record SubmitResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("posted")] bool Posted,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("notes")] IReadOnlyList<NoteView>? Notes,
    [property: JsonPropertyName("undecided_blocks")] IReadOnlyList<string>? UndecidedBlocks);

/// <summary>Enum → the exact snake_case tokens the spec uses on the wire.</summary>
internal static class Wire
{
    public static string Intent(Intent intent) => intent.ToString().ToLowerInvariant();
}
