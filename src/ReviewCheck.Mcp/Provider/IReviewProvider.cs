using ReviewCheck.Core;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// The single seam between the MCP server and block provenance (docs/23 §0).
/// MVP-1 wires <see cref="StubProvider"/>; MVP-2/3 swaps in the Roslyn+graph+LLM
/// pipeline behind the same signature. The 7 tools never change.
/// The exchange type <see cref="AnalyzedReview"/> lives in <c>ReviewCheck.Core</c>.
/// </summary>
public interface IReviewProvider
{
    Task<AnalyzedReview> AnalyzeAsync(Source source);
}
