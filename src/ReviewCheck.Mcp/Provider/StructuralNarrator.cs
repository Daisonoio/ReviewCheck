using ReviewCheck.Core;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Mcp.Provider;

/// <summary>
/// MVP-2 narrator: maps a <see cref="StructuralBlock"/> to a complete <see cref="Block"/> with a
/// DETERMINISTIC narrative built only from the structural facts — describe and ask, never a verdict.
/// Honesty by design: every block declares that this narrative is generated without an LLM
/// (the semantic depth is plan docs/25, MVP-3, which replaces exactly this class).
/// The citations pass through untouched (grounding was computed upstream — GUARDRAILS G2).
/// </summary>
public static class StructuralNarrator
{
    public static Block ToBlock(StructuralBlock structural)
    {
        var what = Capitalize(structural.StructuralFacts.FirstOrDefault()
                              ?? $"changes covered by {structural.Citations[0].File}:{structural.Citations[0].Lines}");

        var why = structural.Intent switch
        {
            Intent.Definition => "This introduces new behavior — the blocks that use it build on what happens here.",
            Intent.Refactor => "Existing behavior changed here — compare it with what you expected it to do.",
            Intent.Wiring => "This connects components together — wiring mistakes compile fine but change runtime behavior.",
            Intent.Config => "These values steer behavior without code — check they match the intent of the change.",
            Intent.Test => "This pins the expected behavior — check the assertions really cover the change.",
            _ => "This code participates in the change — read it in the context of its related blocks.",
        };

        var edges = structural.StructuralFacts
            .Where(f => f.StartsWith("uses ", StringComparison.Ordinal) ||
                        f.StartsWith("used by ", StringComparison.Ordinal))
            .ToList();
        var link = edges.Count > 0
            ? Capitalize(string.Join("; ", edges) + ".")
            : "No graph relations to other blocks in this diff.";

        var uncertainty = AppendSentence(
            structural.UncertaintyStructural,
            "Narrative generated deterministically from code structure (MVP-2, no LLM): it tells you where to look, not what the code means.");

        return new Block(
            Id: structural.Id,
            Title: structural.Title,
            Intent: structural.Intent,
            Code: structural.Code,
            Explanation: new Explanation(what, why, link, structural.Citations, uncertainty),
            RelatedBlockIds: structural.RelatedBlockIds.Count > 0 ? structural.RelatedBlockIds : null,
            EstimatedMinutes: structural.EstimatedMinutes);
    }

    private static string Capitalize(string s) =>
        s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private static string AppendSentence(string? existing, string sentence) =>
        existing is null ? sentence : $"{existing} {sentence}";
}
