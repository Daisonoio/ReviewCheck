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

    /// <summary>
    /// Tries each '{' in <paramref name="raw"/> left to right: finds its BALANCED match (tracking
    /// nesting and skipping over string-literal contents, so a brace inside a quoted value like
    /// `"why": "the map { }"` doesn't throw off the boundary), and attempts to deserialize that span.
    /// The first candidate that parses wins — a naive first-'{'/last-'}' scan grabs the wrong span the
    /// moment the model's surrounding prose contains an unrelated brace pair; this doesn't.
    /// </summary>
    public static bool TryParse(string raw, out LlmExplanation explanation, out string error)
    {
        explanation = new LlmExplanation("", "", "", null);

        var searchFrom = 0;
        while (true)
        {
            var start = raw.IndexOf('{', searchFrom);
            if (start < 0)
            {
                error = "reply contains no JSON object";
                return false;
            }

            if (!TryFindBalancedEnd(raw, start, out var end))
            {
                // Unterminated from here on — no later '{' could close any better than this one did.
                error = "reply contains no JSON object";
                return false;
            }

            try
            {
                var wire = JsonSerializer.Deserialize<Wire>(raw[start..(end + 1)], Options);
                if (wire is not null)
                {
                    explanation = new LlmExplanation(
                        wire.What?.Trim() ?? "",
                        wire.Why?.Trim() ?? "",
                        wire.Link?.Trim() ?? "",
                        string.IsNullOrWhiteSpace(wire.UncertaintySemantic) ? null : wire.UncertaintySemantic.Trim());
                    error = "";
                    return true;
                }
            }
            catch (JsonException)
            {
                // Not the real object (e.g. a stray "{ key: value }" earlier in the model's prose) —
                // fall through and try the next '{' instead of giving up.
            }

            searchFrom = start + 1;
        }
    }

    /// <summary>
    /// From the '{' at <paramref name="start"/>, finds the index of its matching '}'. Returns false if
    /// the object is never closed (e.g. a truncated reply).
    /// </summary>
    private static bool TryFindBalancedEnd(string raw, int start, out int end)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        return true;
                    }
                    break;
            }
        }

        end = -1;
        return false;
    }
}
