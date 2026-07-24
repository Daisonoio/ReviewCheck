namespace ReviewCheck.Platform;

/// <summary>How a file changed in the diff.</summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>
/// One line of a hunk. <c>Kind</c> is ' ' (context), '+' (added), '-' (removed).
/// Line numbers are 1-based; a removed line has no <c>NewNumber</c>, an added line no <c>OldNumber</c>.
/// </summary>
public sealed record DiffLine(char Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>A contiguous change region (<c>@@ -oldStart,oldCount +newStart,newCount @@</c>).</summary>
public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<DiffLine> Lines)
{
    /// <summary>The 1-based new-file line numbers this hunk touches with additions.</summary>
    public IEnumerable<int> AddedNewLines =>
        Lines.Where(l => l.Kind == '+' && l.NewNumber is not null).Select(l => l.NewNumber!.Value);

    /// <summary>The hunk's covered range in the NEW file (start..end inclusive), if it maps to any new lines.</summary>
    public (int Start, int End)? NewRange =>
        NewCount > 0 ? (NewStart, NewStart + NewCount - 1) : null;
}

/// <summary>
/// One changed file. <c>NewText</c> is the file's FULL post-change content (null for deleted or
/// unreadable files) — the pipeline parses it; the hunks say which lines changed.
/// </summary>
public sealed record FileDiff(
    string Path,
    FileChangeKind Kind,
    IReadOnlyList<DiffHunk> Hunks,
    string? OldPath = null,
    string? NewText = null);

/// <summary>The parsed local diff: what <c>ReviewCheck.Pipeline</c> consumes (docs/24 §0).</summary>
public sealed record LocalDiffResult(string Ref, IReadOnlyList<FileDiff> Files);

/// <summary>Seam for the MCP provider: real git in production, in-memory fakes in tests.</summary>
public interface IDiffReader
{
    LocalDiffResult Read(string? @ref);
}
