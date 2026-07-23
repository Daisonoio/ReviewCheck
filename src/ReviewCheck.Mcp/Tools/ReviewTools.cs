using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ReviewCheck.Core;

namespace ReviewCheck.Mcp.Tools;

/// <summary>
/// The 7 MCP tools (spec/mcp-tools.json). Thin adapters over <see cref="ReviewEngine"/>,
/// which carries the logic and the invariants. <c>engine</c> is injected from DI; the
/// remaining parameters are the tools' inputs. Parameter names are the spec's snake_case.
/// </summary>
[McpServerToolType]
public static class ReviewTools
{
    [McpServerTool(Name = "get_review_plan")]
    [Description("Reads a set of changes and returns the plan (blocks, reading order, seams) and the FIRST complete block (code + explanation together). Opens a local session. Mode A (primary) = local pre-PR diff; Mode B = a PR.")]
    public static Task<ReviewPlanResult> GetReviewPlan(ReviewEngine engine, SourceInput source)
        => engine.GetReviewPlanAsync(source.ToSource());

    [McpServerTool(Name = "next_block")]
    [Description("Advances to the next recommended block and returns it COMPLETE (code + explanation together). Print code and explanation together, never separately.")]
    public static NextBlockResult NextBlock(ReviewEngine engine, string session)
        => engine.NextBlock(session);

    [McpServerTool(Name = "get_block")]
    [Description("Returns a specific block COMPLETE (code + explanation together) WITHOUT advancing the pointer. Used to peek at a related block (F4) and to recover co-presence/grounding/uncertainty on demand.")]
    public static GetBlockResult GetBlock(ReviewEngine engine, string session, string block_id)
        => engine.GetBlock(session, block_id);

    [McpServerTool(Name = "accept_block")]
    [Description("The user ACCEPTS the block (they understood it and take responsibility). A human decision recorded in the local state file.")]
    public static AcceptResult AcceptBlock(ReviewEngine engine, string session, string block_id)
        => engine.AcceptBlock(session, block_id);

    [McpServerTool(Name = "request_correction")]
    [Description("The user REQUESTS A CORRECTION on the block, with a note. Marks the block as 'to correct' and stores the note.")]
    public static CorrectionResult RequestCorrection(ReviewEngine engine, string session, string block_id, string note)
        => engine.RequestCorrection(session, block_id, note);

    [McpServerTool(Name = "review_status")]
    [Description("Session status: position, progress, accepted blocks, and blocks to correct.")]
    public static ReviewStatusResult ReviewStatus(ReviewEngine engine, string session)
        => engine.ReviewStatus(session);

    [McpServerTool(Name = "submit_review")]
    [Description("Closes the review. The outcome is the SUM of the per-block human decisions (never an AI verdict). Fails if any block is undecided. Mode A (local): posts nothing, returns a summary + corrections to apply. Mode B (PR): out of scope in MVP-1.")]
    public static SubmitResult SubmitReview(ReviewEngine engine, string session, bool confirm = false)
        => engine.SubmitReview(session, confirm);
}

/// <summary>Input for get_review_plan's <c>source</c> (spec/mcp-tools.json inputSchema).</summary>
public sealed record SourceInput(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ref")] string? Ref = null,
    [property: JsonPropertyName("platform")] string? Platform = null,
    [property: JsonPropertyName("repo")] string? Repo = null,
    [property: JsonPropertyName("pr")] string? Pr = null)
{
    public Source ToSource() => Type switch
    {
        "pull_request" => new Source.PullRequest(Platform ?? "", Repo ?? "", Pr ?? ""),
        _ => new Source.Local(Ref ?? "working"),
    };
}
