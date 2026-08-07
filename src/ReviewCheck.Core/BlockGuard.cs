namespace ReviewCheck.Core;

/// <summary>
/// The single choke point that enforces the co-presence + grounding invariant (docs/13 §1, gate G-SCHEMA):
/// a <see cref="Block"/> is not valid without both <c>Code</c> and at least one citation.
/// Every MCP tool that returns a block passes it through <see cref="Ensure"/>, so even stub fixtures
/// cannot violate the guarantee — this is what makes the R1–R10 recovery commands reliable.
/// </summary>
public static class BlockGuard
{

    /// <summary>Validates the invariant and returns the same block; throws <see cref="InvalidBlockException"/> otherwise.</summary>
    public static Block Ensure(Block block)
    {
        if (block is null)
            throw new InvalidBlockException("Block is null.");

        if (string.IsNullOrWhiteSpace(block.Id))
            throw new InvalidBlockException("Block has no id.");

        if (string.IsNullOrWhiteSpace(block.Code))
            throw new InvalidBlockException($"Block '{block.Id}' has no code (co-presence violated).");

        if (block.Explanation is null)
            throw new InvalidBlockException($"Block '{block.Id}' has no explanation (co-presence violated).");

        if (string.IsNullOrWhiteSpace(block.Explanation.What) ||
            string.IsNullOrWhiteSpace(block.Explanation.Why))
            throw new InvalidBlockException($"Block '{block.Id}' has an empty explanation (co-presence violated).");

        if (block.Explanation.Citations is not { Count: >= 1 })
            throw new InvalidBlockException($"Block '{block.Id}' has no citations (grounding violated).");

        if (block.Explanation.Citations.Any(c =>
                c is null || string.IsNullOrWhiteSpace(c.File) || string.IsNullOrWhiteSpace(c.Lines)))
            throw new InvalidBlockException($"Block '{block.Id}' has an empty citation (grounding violated).");

        var malformed = block.Explanation.Citations.FirstOrDefault(c => !IsWellFormedLineRange(c.Lines));
        if (malformed is not null)
            throw new InvalidBlockException(
                $"Block '{block.Id}' has a malformed citation line range '{malformed.Lines}' " +
                $"(expected \"N\" or \"N-M\" with N <= M, grounding violated).");

        return block;
    }

    /// <summary>Non-throwing check, useful in tests and local oversight signals.</summary>
    public static bool IsValid(Block block)
    {
        try
        {
            Ensure(block);
            return true;
        }
        catch (InvalidBlockException)
        {
            return false;
        }
    }

    /// <summary>
    /// A real citation's line range (<c>AnalysisPipeline.FormatRange</c>) is always a single 1-based
    /// line "N" or a range "N-M" with N &lt;= M. Anything else — non-numeric, zero/negative, or a
    /// backwards range like "15-12" — is structurally present but semantically nonsense, so it isn't
    /// real grounding even though the earlier non-empty check lets it through.
    /// </summary>
    private static bool IsWellFormedLineRange(string lines)
    {
        var parts = lines.Split('-');
        if (parts.Length is not (1 or 2))
            return false;
        if (!int.TryParse(parts[0], out var start) || start < 1)
            return false;
        if (parts.Length == 1)
            return true;

        return int.TryParse(parts[1], out var end) && end >= start;
    }
}

/// <summary>Thrown when a block violates the co-presence or grounding invariant.</summary>
public sealed class InvalidBlockException(string message) : Exception(message);
