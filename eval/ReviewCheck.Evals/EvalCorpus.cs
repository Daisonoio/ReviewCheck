using ReviewCheck.Platform;

namespace ReviewCheck.Evals;

/// <summary>One evaluation case: a set of changed files fed to the real pipeline.</summary>
/// <param name="Name">The corpus subfolder name.</param>
/// <param name="Diff">The synthesized local diff (all files added).</param>
/// <param name="FileLineCounts">Per-file line count, for the "citations are real" check.</param>
public sealed record EvalCase(
    string Name,
    LocalDiffResult Diff,
    IReadOnlyDictionary<string, int> FileLineCounts);

/// <summary>
/// Loads the eval corpus from disk. Each subfolder of <c>corpus/</c> is a case; each <c>.cs</c>
/// file in it is treated as a BRAND-NEW file — the whole file is the change. That is faithful to
/// how ReviewCheck is actually used (an agent adds a feature across new files) and needs no git:
/// the corpus is pure, versioned data, and the same input always produces the same blocks.
/// </summary>
public static class EvalCorpus
{
    public static IReadOnlyList<EvalCase> Load(string corpusDir)
    {
        if (!Directory.Exists(corpusDir))
            throw new DirectoryNotFoundException($"Eval corpus not found at '{corpusDir}'.");

        var cases = new List<EvalCase>();
        foreach (var dir in Directory.GetDirectories(corpusDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var files = new List<FileDiff>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var path in Directory.GetFiles(dir, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(path);
                var rel = $"{Path.GetFileName(dir)}/{Path.GetFileName(path)}";
                var (file, lineCount) = AsAddedFile(rel, text);
                files.Add(file);
                counts[rel] = lineCount;
            }

            if (files.Count > 0)
                cases.Add(new EvalCase(Path.GetFileName(dir), new LocalDiffResult("eval", files), counts));
        }
        return cases;
    }

    /// <summary>Represents a full source file as an added file: one hunk whose '+' lines are the whole file.</summary>
    private static (FileDiff File, int LineCount) AsAddedFile(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var diffLines = lines.Select((l, i) => new DiffLine('+', l, null, i + 1)).ToList();
        var hunk = new DiffHunk(OldStart: 0, OldCount: 0, NewStart: 1, NewCount: lines.Length, diffLines);
        return (new FileDiff(path, FileChangeKind.Added, [hunk], NewText: text), lines.Length);
    }
}
