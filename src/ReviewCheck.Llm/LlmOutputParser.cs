using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewCheck.Llm;

/// <summary>
/// Parses the model's reply into an <see cref="LlmExplanation"/> (docs/25 T4). Tolerant on the
/// wrapping — models love markdown fences and stray prose — but strict on the substance: if no
/// JSON object can be extracted, this returns false with a reason, and the caller retries or
/// degrades (never crashes — the T4 stop-if). Missing fields become empty strings so the
/// RUBRIC rejects them with a precise violation, keeping "unparseable" and "incomplete" as
/// two distinct, reportable failures.
/// </summary>
public static class LlmOutputParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Wire(
        [property: JsonPropertyName("what")] string? What,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("uncertainty_semantic")] string? UncertaintySemantic);

    public static bool TryParse(string raw, out LlmExplanation explanation, out string error)
    {
        explanation = new LlmExplanation("", "", "", null);

        // Take the outermost {...} — whatever fences or prose surround it.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "reply contains no JSON object";
            return false;
        }

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(raw[start..(end + 1)], Options);
            if (wire is null)
            {
                error = "reply parsed to null";
                return false;
            }

            explanation = new LlmExplanation(
                wire.What?.Trim() ?? "",
                wire.Why?.Trim() ?? "",
                wire.Link?.Trim() ?? "",
                string.IsNullOrWhiteSpace(wire.UncertaintySemantic) ? null : wire.UncertaintySemantic.Trim());
            error = "";
            return true;
        }
        catch (JsonException e)
        {
            error = $"reply is not valid JSON: {e.Message}";
            return false;
        }
    }
}
