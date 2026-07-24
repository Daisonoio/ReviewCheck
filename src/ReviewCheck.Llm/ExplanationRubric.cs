using System.Text.RegularExpressions;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Llm;

/// <summary>
/// The bouncer (docs/25 §5): every LLM output passes here BEFORE becoming a Block. Checks are
/// deliberately mechanical — presence, verdict vocabulary, hallucinated file references — so
/// they are deterministic and testable; the subtle judgment stays with the human, which is the
/// whole point. A violation is returned as a sentence that the retry prompt quotes back to the
/// model. Null = the output may become a Block.
/// </summary>
public static partial class ExplanationRubric
{
    // "correct/safe/approved/it's a bug" as judgments (G-NOVERDICT on language). The list errs
    // toward strictness: a false positive costs one retry, a false negative hands the user a verdict.
    [GeneratedRegex(
        @"\b(correct(ly)?|incorrect(ly)?|safe(ly)?|unsafe|secure(ly)?|insecure|approved?|bug(gy|s)?|vulnerabilit(y|ies)|vulnerable|flaw(ed|s)?|wrong(ly)?|broken)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VerdictLanguage();

    // File-looking tokens ("Foo.cs", "appsettings.json") — each must exist in the provided material.
    [GeneratedRegex(
        @"\b[\w/.-]+\.(cs|csproj|json|ya?ml|xml|md|txt|config|props|targets|sln|razor|cshtml)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileReference();

    /// <summary>Returns the violated rule as a sentence, or null when the output is acceptable.</summary>
    public static string? Violation(StructuralBlock block, LlmExplanation e)
    {
        // Presence: what/why non-empty and non-trivial (docs/25 §5 row 1).
        if (e.What.Length < 10)
            return "'what' is empty or trivial — 1-2 real sentences are required";
        if (e.Why.Length < 10)
            return "'why' is empty or trivial — 1-2 real sentences are required";
        if (e.Link.Length == 0)
            return "'link' is missing — state the connections from the provided facts, or that there are none";

        // No-verdict: the assertive fields must describe, never judge (row 2).
        // uncertainty_semantic is exempt: doubt is the honesty channel, not a verdict.
        var assertive = $"{e.What}\n{e.Why}\n{e.Link}";
        if (VerdictLanguage().Match(assertive) is { Success: true } verdict)
            return $"verdict language ('{verdict.Value}') — describe and ask; the human judges, not you";

        // Grounded references: no files outside the provided code/citations/facts (rows 3-4).
        var known = string.Join('\n',
            block.Citations.Select(c => c.File)
                .Append(block.Code)
                .Append(block.Title)
                .Concat(block.StructuralFacts));
        var everything = $"{assertive}\n{e.UncertaintySemantic}";
        foreach (Match reference in FileReference().Matches(everything))
            if (!known.Contains(reference.Value, StringComparison.OrdinalIgnoreCase))
                return $"references '{reference.Value}', which is not in the provided code, citations, or facts";

        return null;
    }
}
