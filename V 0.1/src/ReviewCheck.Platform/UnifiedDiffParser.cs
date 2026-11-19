namespace ReviewCheck.Platform;

/// <summary>
/// Deterministic parser for git's unified diff format. Pure text-in → model-out
/// (no git, no filesystem), so it is golden-testable. Tolerant of the metadata
/// lines git interleaves (index, mode, similarity, "\ No newline at end of file").
/// </summary>
public static class UnifiedDiffParser
{
    public static IReadOnlyList<FileDiff> Parse(string diffText)
    {
        var files = new List<FileDiff>();
        if (string.IsNullOrWhiteSpace(diffText))
            return files;

        var lines = diffText.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var (file, next) = ParseFileSection(lines, i);
            files.Add(file);
            i = next;
        }

        return files;
    }

    private static (FileDiff File, int Next) ParseFileSection(string[] lines, int start)
    {
        var kind = FileChangeKind.Modified;
        string? oldPath = null;
        string? newPath = null;
        var hunks = new List<DiffHunk>();

        // Fallback paths from the "diff --git a/X b/Y" header itself.
        var header = lines[start];
        var headerNew = HeaderPath(header);

        var i = start + 1;
        for (; i < lines.Length && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal); i++)
        {
            var line = TrimCr(lines[i]);

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
                kind = FileChangeKind.Added;
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                kind = FileChangeKind.Deleted;
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                kind = FileChangeKind.Renamed;
                oldPath = line["rename from ".Length..];
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                newPath = line["rename to ".Length..];
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
                oldPath ??= StripPrefix(line[4..]);
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                newPath ??= StripPrefix(line[4..]);
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var (hunk, next) = ParseHunk(lines, i);
                hunks.Add(hunk);
                i = next - 1; // for-loop increments
            }
        }

        var path = newPath is null or "/dev/null" ? (oldPath ?? headerNew) : newPath;
        return (new FileDiff(path, kind, hunks, kind == FileChangeKind.Renamed ? oldPath : null), i);
    }

    private static (DiffHunk Hunk, int Next) ParseHunk(string[] lines, int start)
    {
        // "@@ -oldStart[,oldCount] +newStart[,newCount] @@ optional section"
        var head = lines[start];
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (oldStart, oldCount) = ParseRange(parts[1][1..]); // strip '-'
        var (newStart, newCount) = ParseRange(parts[2][1..]); // strip '+'

        var body = new List<DiffLine>();
        var oldNo = oldStart;
        var newNo = newStart;

        var i = start + 1;
        for (; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal) || raw.StartsWith("@@", StringComparison.Ordinal))
                break;
            if (raw.StartsWith('\\')) // "\ No newline at end of file"
                continue;
            if (raw.Length == 0 && i == lines.Length - 1)
                break; // trailing split artifact

            var kind = raw.Length > 0 ? raw[0] : ' ';
            var text = TrimCr(raw.Length > 0 ? raw[1..] : "");
            switch (kind)
            {
                case '+':
                    body.Add(new DiffLine('+', text, null, newNo++));
                    break;
                case '-':
                    body.Add(new DiffLine('-', text, oldNo++, null));
                    break;
                case ' ':
                    body.Add(new DiffLine(' ', text, oldNo++, newNo++));
                    break;
                default:
                    // Unknown marker: end of hunk (e.g. commit message lines in `git show`).
                    return (new DiffHunk(oldStart, oldCount, newStart, newCount, body), i);
            }

            if (oldNo >= oldStart + oldCount && newNo >= newStart + newCount)
            {
                i++;
                break;
            }
        }

        return (new DiffHunk(oldStart, oldCount, newStart, newCount, body), i);
    }

    private static (int Start, int Count) ParseRange(string token)
    {
        var comma = token.IndexOf(',');
        return comma < 0
            ? (int.Parse(token), 1)
            : (int.Parse(token[..comma]), int.Parse(token[(comma + 1)..]));
    }

    /// <summary>"a/src/File.cs" → "src/File.cs"; "/dev/null" stays as-is.</summary>
    private static string StripPrefix(string p)
    {
        p = TrimCr(p);
        return p.StartsWith("a/", StringComparison.Ordinal) || p.StartsWith("b/", StringComparison.Ordinal)
            ? p[2..]
            : p;
    }

    private static string HeaderPath(string header)
    {
        // "diff --git a/X b/Y" → Y (best-effort fallback; quoted paths out of MVP scope)
        var idx = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return idx >= 0 ? TrimCr(header[(idx + 3)..]) : header;
    }

    private static string TrimCr(string s) => s.EndsWith('\r') ? s[..^1] : s;
}
