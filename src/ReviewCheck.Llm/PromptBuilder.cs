using System.Text;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm;

/// <summary>
/// Builds the two prompts (docs/25 §4). The SYSTEM prompt carries the fixed rules — explain
/// don't judge, anchor only to what is provided, declare uncertainty, JSON out — and, on a
/// retry, the rule the previous output violated. The USER prompt carries one block's facts:
/// code, fixed citations, structural facts, structural uncertainty. Nothing here ever asks
/// for a judgment (T3 stop-if) — the words "review", "assess", "verify" are deliberately
/// absent from the instructions given to the model.
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
        2. Anchor everything to the provided code and citation lines. Never mention files,
           symbols, or behavior that do not appear in the provided code, citations, or facts.
        3. Mention relations to other blocks ONLY if they appear in the provided structural
           facts. If none are provided, say there are none.
        4. When you are unsure, say so in uncertainty_semantic instead of guessing.

        Reply with ONLY a JSON object in exactly this shape (no markdown, no fences, no prose
        around it):
        {"what": "1-2 sentences: what this block does",
         "why": "1-2 sentences: why it exists / what it connects to",
         "link": "its connections, built only from the provided facts; or a sentence saying there are none",
         "uncertainty_semantic": "where you are genuinely unsure, or null"}
        """;

    /// <summary>The fixed rules; when retrying, the violated rule is put in front (docs/25 §5: retry once, stricter).</summary>
    public static string System(string? previousViolation = null) =>
        previousViolation is null
            ? Rules
            : $"PREVIOUS ATTEMPT REJECTED: {previousViolation}.\nReply again and follow every rule exactly.\n\n{Rules}";

    /// <summary>One block's facts — the only material the model may narrate from.</summary>
    public static string User(StructuralBlock block)
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
        sb.AppendLine("STRUCTURAL FACTS (the only relations you may mention):");
        if (block.StructuralFacts.Count == 0)
            sb.AppendLine("- (none)");
        else
            foreach (var f in block.StructuralFacts)
                sb.AppendLine($"- {f}");

        if (block.UncertaintyStructural is not null)
        {
            sb.AppendLine();
            sb.AppendLine("STRUCTURAL UNCERTAINTY (already known; do not contradict it):");
            sb.AppendLine($"- {block.UncertaintyStructural}");
        }

        return sb.ToString();
    }
}
