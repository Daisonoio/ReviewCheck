using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewCheck.Evals;

/// <summary>One capability's aggregate score across the corpus. Threshold is 100% by design:
/// these are safety guarantees, so any single failure is a capability regression.</summary>
public sealed record CapabilityScore(string Capability, int Passed, int Total)
{
    [JsonIgnore] public bool Ok => Passed == Total;
    public double Rate => Total == 0 ? 1.0 : (double)Passed / Total;
}

/// <summary>Aggregates <see cref="CheckResult"/>s into a per-capability scorecard + a JSON report.</summary>
public sealed record Scorecard(IReadOnlyList<CapabilityScore> Scores, IReadOnlyList<CheckResult> Failures)
{
    [JsonIgnore] public bool AllOk => Scores.All(s => s.Ok);

    public static Scorecard From(IEnumerable<CheckResult> results)
    {
        var all = results.ToList();
        var scores = all
            .GroupBy(r => r.Capability)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CapabilityScore(g.Key, g.Count(r => r.Passed), g.Count()))
            .ToList();
        var failures = all.Where(r => !r.Passed).ToList();
        return new Scorecard(scores, failures);
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ReviewCheck — agent-capability scorecard");
        sb.AppendLine("========================================");
        sb.AppendLine($"{"CAPABILITY",-30} {"PASS",6} {"RATE",7}  STATUS");
        foreach (var s in Scores)
            sb.AppendLine($"{s.Capability,-30} {$"{s.Passed}/{s.Total}",6} {s.Rate,6:P0}  {(s.Ok ? "PASS" : "FAIL")}");

        if (Failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            foreach (var f in Failures)
                sb.AppendLine($"  [{f.Capability}] {f.Case}: {f.Detail}");
        }

        sb.AppendLine();
        sb.AppendLine(AllOk
            ? $"RESULT: PASS — {Scores.Count} capabilities upheld across the corpus."
            : $"RESULT: FAIL — {Failures.Count} check(s) regressed.");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
