using ReviewCheck.Core;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// The result of analyzing a source: an ordered list of blocks (order = index) plus
/// the seams between them. This is the seam type of docs/23 §1.1 — the MCP tools
/// consume it without knowing whether it came from fixtures or the real pipeline.
/// </summary>
public sealed record AnalyzedReview(
    string Title,
    IReadOnlyList<Block> Blocks,
    IReadOnlyList<InteractionPoint> InteractionPoints,
    int? EstimatedMinutes = null);

/// <summary>
/// The single seam between the MCP server and block provenance (docs/23 §0).
/// MVP-1 wires <see cref="StubProvider"/>; MVP-2/3 swaps in the Roslyn+graph+LLM
/// pipeline behind the same signature. The 7 tools never change.
/// </summary>
public interface IReviewProvider
{
    Task<AnalyzedReview> AnalyzeAsync(Source source);
}
