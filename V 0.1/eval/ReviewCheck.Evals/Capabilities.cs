using System.Text.RegularExpressions;
using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Pipeline;

namespace ReviewCheck.Evals;

/// <summary>The outcome of one capability check on one case.</summary>
public sealed record CheckResult(string Capability, string Case, bool Passed, string? Detail);

/// <summary>
/// The capability checks — one guardrail per capability, evaluated as a PROPERTY over the corpus.
/// Two tiers:
/// <list type="bullet">
///   <item><b>Structural</b> — run the real pipeline + the facts narrator, then assert the
///   deterministic guarantees (co-presence, real citations, no verdict, reading order, declared
///   uncertainty). Offline and reproducible: same corpus in, same verdict out.</item>
///   <item><b>LLM-behaviour</b> — drive the adapter with a SCRIPTED model emitting bad output and
///   assert the guardrail net catches it (verdict/hallucination rejected, degrade-on-unavailable,
///   citations stapled verbatim).</item>
/// </list>
/// The checks judge INDEPENDENTLY of the implementation — e.g. the verdict word list here is the
/// eval's own, not the rubric's regex — so a bug in the rubric can't hide from its own eval.
/// </summary>
public static partial class Capabilities
{
    // The eval's OWN verdict vocabulary (independent of ExplanationRubric on purpose).
    [GeneratedRegex(
        @"\b(correct(ly)?|incorrect(ly)?|safe(ly)?|unsafe|secure(ly)?|insecure|approved?|bug(gy|s)?|vulnerabilit(y|ies)|vulnerable|flaw(ed|s)?|wrong(ly)?|broken)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Verdict();

    // ---------------- Tier A: structural capabilities ----------------

    public static IEnumerable<CheckResult> Structural(
        EvalCase @case, IReadOnlyList<Block> blocks, PipelineResult pipeline)
    {
        yield return CoPresence(@case, blocks);
        yield return GroundingReal(@case, blocks);
        yield return NoVerdict(@case, blocks);

        // Reading order applies only where the graph produced at least one relation.
        if (pipeline.Relations.Count > 0)
            yield return ReadingOrder(@case, blocks, pipeline);

        // Declared uncertainty is expected of cases labelled "external*" (a real external dependency).
        if (@case.Name.StartsWith("external", StringComparison.OrdinalIgnoreCase))
            yield return DeclaredUncertainty(@case, blocks);
    }

    private static CheckResult CoPresence(EvalCase c, IReadOnlyList<Block> blocks)
    {
        var bad = blocks.FirstOrDefault(b => !BlockGuard.IsValid(b));
        return new CheckResult("co-presence", c.Name, bad is null,
            bad is null ? null : $"block '{bad.Id}' fails co-presence/grounding");
    }

    private static CheckResult GroundingReal(EvalCase c, IReadOnlyList<Block> blocks)
    {
        foreach (var b in blocks)
        foreach (var cite in b.Explanation.Citations)
        {
            if (!c.FileLineCounts.TryGetValue(cite.File, out var lineCount))
                return new CheckResult("grounding-real", c.Name, false,
                    $"block '{b.Id}' cites unknown file '{cite.File}'");

            var (start, end) = ParseRange(cite.Lines);
            // +1 slack absorbs trailing-newline differences; the point is to catch fabricated ranges.
            if (start < 1 || end > lineCount + 1 || start > end)
                return new CheckResult("grounding-real", c.Name, false,
                    $"block '{b.Id}' cites {cite.File}:{cite.Lines} outside 1..{lineCount}");
        }
        return new CheckResult("grounding-real", c.Name, true, null);
    }

    private static CheckResult NoVerdict(EvalCase c, IReadOnlyList<Block> blocks)
    {
        foreach (var b in blocks)
        {
            var assertive = $"{b.Explanation.What}\n{b.Explanation.Why}\n{b.Explanation.Link}";
            if (Verdict().Match(assertive) is { Success: true } m)
                return new CheckResult("no-verdict", c.Name, false,
                    $"block '{b.Id}' contains verdict word '{m.Value}'");
        }
        return new CheckResult("no-verdict", c.Name, true, null);
    }

    private static CheckResult ReadingOrder(EvalCase c, IReadOnlyList<Block> blocks, PipelineResult pipeline)
    {
        var index = blocks.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
        // Relation is (From = user, To = used-definition): the definition must be read first.
        foreach (var rel in pipeline.Relations)
            if (index.TryGetValue(rel.From, out var user) && index.TryGetValue(rel.To, out var used) && used > user)
                return new CheckResult("reading-order", c.Name, false,
                    $"'{rel.To}' (definition) is read after '{rel.From}' (user)");
        return new CheckResult("reading-order", c.Name, true, null);
    }

    private static CheckResult DeclaredUncertainty(EvalCase c, IReadOnlyList<Block> blocks)
    {
        var declared = blocks.Any(b =>
            b.Explanation.Uncertainty?.Contains("Unresolved", StringComparison.OrdinalIgnoreCase) == true);
        return new CheckResult("declared-uncertainty", c.Name, declared,
            declared ? null : "no block declared uncertainty for the external dependency");
    }

    // ---------------- Tier B: LLM-behaviour capabilities ----------------

    /// <summary>
    /// Drives the adapter with a scripted model over one representative block, checking that the
    /// guardrail net holds regardless of what the model says.
    /// </summary>
    public static async Task<IReadOnlyList<CheckResult>> LlmBehaviour(
        string caseName, StructuralBlock block, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();

        // Verdict rejection: a confident, verdict-laden reply (twice) must never reach the user.
        var verdict = ScriptedLlmProvider.Json(
            "This discount code is correct and completely safe to ship.",
            "The implementation is correct and free of bugs.",
            "Nothing else to check.");
        var afterVerdict = await Narrate(new ScriptedLlmProvider().Returns(verdict).Returns(verdict), block, ct);
        var leaked = Verdict().Match($"{afterVerdict.Explanation.What}\n{afterVerdict.Explanation.Why}\n{afterVerdict.Explanation.Link}");
        results.Add(new CheckResult("llm:verdict-rejected", caseName, !leaked.Success,
            leaked.Success ? $"verdict '{leaked.Value}' reached the block" : null));

        // Hallucination rejection: a reply citing a file not in the material must be caught.
        var halluc = ScriptedLlmProvider.Json(
            "Loads the discount ceiling from Secrets.json before applying it.",
            "Reads external configuration to decide the cap.",
            "Depends on Secrets.json.");
        var afterHalluc = await Narrate(new ScriptedLlmProvider().Returns(halluc).Returns(halluc), block, ct);
        // Inspect only the ASSERTIVE fields: the honest uncertainty channel is allowed (and expected)
        // to name the dropped reference when explaining why it degraded — that is not a leak.
        var hallucLeaked = ContainsAssertive(afterHalluc, "Secrets.json");
        results.Add(new CheckResult("llm:hallucination-rejected", caseName, !hallucLeaked,
            hallucLeaked ? "hallucinated 'Secrets.json' reached an assertive field" : null));

        // Degrade on unavailable: an unavailable model must still yield a valid (facts) block.
        var afterDown = await Narrate(new ScriptedLlmProvider().Fails(), block, ct);
        results.Add(new CheckResult("llm:degrade-on-unavailable", caseName, BlockGuard.IsValid(afterDown),
            BlockGuard.IsValid(afterDown) ? null : "no valid block after the model was unavailable"));

        // Citations verbatim: even a VALID reply cannot move the anchors — they come from the pipeline.
        var valid = ScriptedLlmProvider.Json(
            "Applies the requested percentage off the price after capping it.",
            "It defers the maximum-percentage bound to the shared clamp helper before computing the amount.",
            "Relies on the pricing-rules clamp for the bound.");
        var afterValid = await Narrate(new ScriptedLlmProvider().Returns(valid), block, ct);
        var verbatim = afterValid.Explanation.Citations.Select(x => $"{x.File}:{x.Lines}")
            .SequenceEqual(block.Citations.Select(x => $"{x.File}:{x.Lines}"));
        results.Add(new CheckResult("llm:citations-verbatim", caseName, verbatim,
            verbatim ? null : "the model's block did not carry the pipeline's citations verbatim"));

        return results;
    }

    private static async Task<Block> Narrate(ScriptedLlmProvider provider, StructuralBlock block, CancellationToken ct)
    {
        var blocks = await new LlmAdapter(provider).NarrateAsync([block], ct);
        return blocks[0];
    }

    // Only the fields ReviewCheck presents as assertions — NOT the uncertainty channel, which may
    // legitimately narrate that a bad reference was dropped.
    private static bool ContainsAssertive(Block b, string needle) =>
        $"{b.Explanation.What}\n{b.Explanation.Why}\n{b.Explanation.Link}"
            .Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static (int Start, int End) ParseRange(string lines)
    {
        var dash = lines.IndexOf('-');
        if (dash < 0)
            return (int.Parse(lines), int.Parse(lines));
        return (int.Parse(lines[..dash]), int.Parse(lines[(dash + 1)..]));
    }
}
