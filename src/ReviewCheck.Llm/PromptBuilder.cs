using System.Text;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm;

/// <summary>
/// Builds the two prompts (docs/25 §4). The SYSTEM prompt carries the fixed rules — explain
/// don't judge, anchor only to what is provided, declare uncertainty, JSON out — and, on a
/// retry, the rule the previous output violated. The USER prompt carries one block's facts
/// (code, fixed citations, structural facts, structural uncertainty) plus the FULL code of the
/// related blocks, so interaction narrative can be concrete. Nothing here ever asks for a
/// judgment (T3 stop-if) — the words "review", "assess", "verify" are deliberately absent from
/// the instructions given to the model.
/// </summary>
public static class PromptBuilder
{
    private const string Rules =
        """
        You explain code so a human reviewer can genuinely understand it. You never judge it —
        the human decides; you make the code understandable.

        Hard rules:
        1. Explain, don't judge. Never state or imply that the code is correct, safe, approved,
           buggy, vulnerable, or wrong. Describe what it does and why it exists; the human draws
           every conclusion.
        2. Anchor everything to the provided code, citation lines, facts, and related blocks.
           Never mention files, symbols, or behavior absent from the provided material.
        3. Describe relations to other blocks using the provided facts and related-block code.
           If there are none, say so.
        4. When you are unsure, say so in uncertainty_semantic instead of guessing.

        Be specific, not generic. This is the difference between a useful explanation and a
        useless one:
        - Name the actual identifiers — the methods, variables, types, constants involved.
        - Describe the concrete operation: the transformation, the condition, the data flow —
          not a category label. Avoid empty verbs ("handles", "manages", "processes", "deals
          with") unless you immediately say what is handled and how.
        - When the block calls into a related block, name the exact member and what comes back.

        Example — WEAK (do not write like this):
          {"what": "This method processes a discount for a price.",
           "why": "It handles the discount logic for the application."}
        Example — STRONG (write like this):
          {"what": "ApplyDiscount clamps the percent to the 0..MaxDiscountPercent range via
                    PricingRules.Clamp, then subtracts that percentage of price and returns the result.",
           "why": "It centralizes the discount math so callers pass a raw percent and get a
                   bounded reduction; the bound comes from PricingRules, not from the caller."}

        Reply with ONLY a JSON object in exactly this shape (no markdown, no fences, no prose
        around it):
        {"what": "1-3 sentences: what this block concretely does, naming the real identifiers",
         "why": "1-3 sentences: why it exists / what it connects to",
         "link": "its concrete connections to the related blocks; or a sentence saying there are none",
         "uncertainty_semantic": "where you are genuinely unsure, or null"}
        """;

    /// <summary>The fixed rules; when retrying, the violated rule is put in front (docs/25 §5: retry once, stricter).</summary>
    public static string System(string? previousViolation = null) =>
        previousViolation is null
            ? Rules
            : $"PREVIOUS ATTEMPT REJECTED: {previousViolation}.\nReply again and follow every rule exactly.\n\n{Rules}";

    /// <summary>
    /// One block's facts plus the FULL code of its related blocks — the material the model may
    /// narrate from. <paramref name="byId"/> resolves <see cref="StructuralBlock.RelatedBlockIds"/>
    /// to their blocks; ids with no entry (e.g. the block itself, or ids outside the map) are skipped.
    /// </summary>
    public static string User(StructuralBlock block, IReadOnlyDictionary<string, StructuralBlock> byId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Explain this changed code block.");
        sb.AppendLine();
        sb.AppendLine($"TITLE: {block.Title}");
        sb.AppendLine($"INTENT: {block.Intent}");
        sb.AppendLine();
        sb.AppendLine("CODE:");
        sb.AppendLine(block.Code);
        sb.AppendLine();
        sb.AppendLine("CITATIONS (the lines this block covers — anchor to these only):");
        foreach (var c in block.Citations)
            sb.AppendLine($"- {c.File} lines {c.Lines}");

        sb.AppendLine();
        sb.AppendLine("STRUCTURAL FACTS (verified relations you may rely on):");
        if (block.StructuralFacts.Count == 0)
            sb.AppendLine("- (none)");
        else
            foreach (var f in block.StructuralFacts)
                sb.AppendLine($"- {f}");

        var related = RelatedBlocks(block, byId);
        if (related.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELATED BLOCKS (context — explain THIS block, do not re-explain these,");
            sb.AppendLine("but name the exact members you reference from them):");
            foreach (var r in related)
            {
                sb.AppendLine();
                sb.AppendLine($"--- {r.Title} ---");
                sb.AppendLine(r.Code);
            }
        }

        if (block.UncertaintyStructural is not null)
        {
            sb.AppendLine();
            sb.AppendLine("STRUCTURAL UNCERTAINTY (already known; do not contradict it):");
            sb.AppendLine($"- {block.UncertaintyStructural}");
        }

        return sb.ToString();
    }

    /// <summary>The block's related blocks resolved from the id map, in id order, self excluded.</summary>
    internal static List<StructuralBlock> RelatedBlocks(
        StructuralBlock block, IReadOnlyDictionary<string, StructuralBlock> byId) =>
        block.RelatedBlockIds
            .Where(id => id != block.Id && byId.ContainsKey(id))
            .Select(id => byId[id])
            .ToList();
}
