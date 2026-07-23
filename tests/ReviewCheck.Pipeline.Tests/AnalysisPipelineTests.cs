using ReviewCheck.Core;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;

namespace ReviewCheck.Pipeline.Tests;

/// <summary>
/// Golden tests for the analysis pipeline (docs/24 §5, T2–T10): a fixed in-memory diff
/// (class + top-level wiring + test + json) → the expected deterministic structure.
/// Covers: P1 hunk→symbol, P2 cross-file graph, P3 segmentation, P4 order,
/// P5 citations, P6 relations+seams, P7 uncertainty, P8 degradation.
/// </summary>
public class AnalysisPipelineTests
{
    // ---- fixed input (the golden diff) ----

    private const string TokenBucketCs =
        """
        namespace RateLimiting;

        public sealed class TokenBucket
        {
            private readonly int _capacity;

            public TokenBucket(int capacity) => _capacity = capacity;

            public bool Allow(string key)
            {
                return Registry.Count(key) < _capacity;
            }
        }
        """;

    private const string ProgramCs =
        """
        using RateLimiting;

        var bucket = new TokenBucket(100);
        System.Console.WriteLine(bucket.Allow("k"));
        """;

    private const string TestsCs =
        """
        using RateLimiting;
        using Xunit;

        public class TokenBucketTests
        {
            [Fact]
            public void Allows_under_capacity()
            {
                var bucket = new TokenBucket(1);
                Assert.True(bucket.Allow("k"));
            }
        }
        """;

    private const string AppSettingsJson =
        """
        {
          "RateLimit": { "Capacity": 100 }
        }
        """;

    private static LocalDiffResult GoldenDiff() => new("working",
    [
        new FileDiff("src/RateLimiting/TokenBucket.cs", FileChangeKind.Modified,
            [Hunk(newStart: 8, added: [9, 10, 11, 12])],
            NewText: TokenBucketCs),
        new FileDiff("src/Program.cs", FileChangeKind.Added,
            [Hunk(newStart: 1, added: [1, 2, 3, 4])],
            NewText: ProgramCs),
        new FileDiff("tests/TokenBucketTests.cs", FileChangeKind.Added,
            [Hunk(newStart: 1, added: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])],
            NewText: TestsCs),
        new FileDiff("appsettings.json", FileChangeKind.Added,
            [Hunk(newStart: 1, added: [1, 2, 3])],
            NewText: AppSettingsJson),
    ]);

    /// <summary>A hunk whose lines are all additions at the given new-file line numbers (plus one leading context line).</summary>
    private static DiffHunk Hunk(int newStart, int[] added)
    {
        var lines = new List<DiffLine>();
        if (newStart > 1)
            lines.Add(new DiffLine(' ', "", newStart - 1, newStart));
        lines.AddRange(added.Select(n => new DiffLine('+', $"line {n}", null, n)));
        var start = newStart > 1 ? newStart : added[0];
        var count = (newStart > 1 ? 1 : 0) + added.Length;
        return new DiffHunk(start - (newStart > 1 ? 0 : 0), count, start, count, lines);
    }

    private static PipelineResult Run() => new AnalysisPipeline().Run(GoldenDiff());

    // ---- T4/P3: segmentation ----

    [Fact]
    public void Segments_IntoExpectedBlocks_NotFiles()
    {
        var result = Run();

        Assert.Equal(4, result.Blocks.Count);
        Assert.Contains(result.Blocks, b => b.Title == "Method TokenBucket.Allow — added");
        Assert.Contains(result.Blocks, b => b.Title.Contains("Program.cs — top-level changes"));
        Assert.Contains(result.Blocks, b => b.Title.Contains("Allows_under_capacity"));
        Assert.Contains(result.Blocks, b => b.Title.Contains("appsettings.json"));
    }

    [Fact]
    public void Intents_AreInferred()
    {
        var result = Run();

        Assert.Equal(Intent.Definition, ByTitle(result, "TokenBucket.Allow").Intent);
        Assert.Equal(Intent.Wiring, ByTitle(result, "Program.cs").Intent);
        Assert.Equal(Intent.Test, ByTitle(result, "Allows_under_capacity").Intent);
        Assert.Equal(Intent.Config, ByTitle(result, "appsettings.json").Intent);
    }

    // ---- T5/P4: reading order ----

    [Fact]
    public void Order_DefinitionsPrecedeUses()
    {
        var result = Run();
        var ids = result.Blocks.Select(b => b.Id).ToList();

        var definition = ByTitle(result, "TokenBucket.Allow");
        var wiring = ByTitle(result, "Program.cs");
        var test = ByTitle(result, "Allows_under_capacity");

        Assert.True(ids.IndexOf(definition.Id) < ids.IndexOf(wiring.Id),
            "the definition must come before the wiring that uses it");
        Assert.True(ids.IndexOf(definition.Id) < ids.IndexOf(test.Id),
            "the definition must come before the test that exercises it");
    }

    [Fact]
    public void Ids_FollowFinalOrder()
    {
        var result = Run();
        Assert.Equal(result.Blocks.Select((_, i) => $"b{i + 1}"), result.Blocks.Select(b => b.Id));
    }

    // ---- T6/P5: citations ----

    [Fact]
    public void EveryBlock_HasAtLeastOneCitation_ToRealLines()
    {
        var result = Run();
        var fileLengths = GoldenDiff().Files.ToDictionary(f => f.Path, f => f.NewText!.Split('\n').Length);

        Assert.All(result.Blocks, b =>
        {
            Assert.NotEmpty(b.Citations);
            Assert.All(b.Citations, c =>
            {
                var (start, end) = ParseRange(c.Lines);
                Assert.InRange(start, 1, fileLengths[c.File]);
                Assert.InRange(end, start, fileLengths[c.File]);
            });
        });
    }

    [Fact]
    public void AllowBlock_CitesTheMethodLines()
    {
        var allow = ByTitle(Run(), "TokenBucket.Allow");
        var citation = Assert.Single(allow.Citations);
        Assert.Equal(("src/RateLimiting/TokenBucket.cs", "9-12"), (citation.File, citation.Lines));
    }

    // ---- T7/P6: relations and seams ----

    [Fact]
    public void Graph_FindsUsesAndTestsEdges_CrossFile()
    {
        var result = Run();
        var definition = ByTitle(result, "TokenBucket.Allow");
        var wiring = ByTitle(result, "Program.cs");
        var test = ByTitle(result, "Allows_under_capacity");

        Assert.Contains(result.Relations, r =>
            r.From == wiring.Id && r.To == definition.Id && r.Kind == RelationKind.Uses);
        Assert.Contains(result.Relations, r =>
            r.From == test.Id && r.To == definition.Id && r.Kind == RelationKind.Tests);

        Assert.Contains(definition.Id, wiring.RelatedBlockIds);
        Assert.Contains(wiring.Id, definition.RelatedBlockIds);
    }

    [Fact]
    public void Seams_ComeFromGraphEdges_AndReferenceRealBlocks()
    {
        var result = Run();
        var ids = result.Blocks.Select(b => b.Id).ToHashSet();

        Assert.Equal(result.Relations.Count, result.InteractionPoints.Count);
        Assert.All(result.InteractionPoints, p => Assert.All(p.BlockIds, id => Assert.Contains(id, ids)));
    }

    [Fact]
    public void NoEdges_MeansNoSeams()
    {
        var lonely = new LocalDiffResult("working",
            [new FileDiff("notes.txt", FileChangeKind.Added, [Hunk(1, [1])], NewText: "hello\n")]);

        var result = new AnalysisPipeline().Run(lonely);

        Assert.Empty(result.Relations);
        Assert.Empty(result.InteractionPoints); // never invented (docs/24 P6)
    }

    // ---- T8/P7: structural uncertainty ----

    [Fact]
    public void UnresolvedExternalSymbol_IsDeclaredNotGuessed()
    {
        var allow = ByTitle(Run(), "TokenBucket.Allow");

        Assert.NotNull(allow.UncertaintyStructural);
        Assert.Contains("Registry", allow.UncertaintyStructural);
    }

    // ---- T9/P8: graceful degradation ----

    [Fact]
    public void NonCSharpFile_DegradesToPerHunkBlock_NoCrash()
    {
        var json = ByTitle(Run(), "appsettings.json");

        Assert.Equal(Intent.Config, json.Intent);
        Assert.NotNull(json.UncertaintyStructural);
        Assert.Contains("Structural analysis unavailable", json.UncertaintyStructural);
        Assert.Equal("1-3", Assert.Single(json.Citations).Lines);
    }

    [Fact]
    public void UnparsableCSharp_DoesNotCrash()
    {
        var broken = new LocalDiffResult("working",
            [new FileDiff("Broken.cs", FileChangeKind.Added, [Hunk(1, [1, 2])], NewText: "class {{{ not c#")]);

        var result = new AnalysisPipeline().Run(broken); // degraded or file-level — but never throws
        Assert.NotNull(result);
    }

    // ---- T10: orchestration, determinism ----

    [Fact]
    public void Deterministic_TwoRunsAreIdentical()
    {
        Assert.Equal(Project(Run()), Project(Run()));

        static string Project(PipelineResult r) => string.Join("|",
            r.Blocks.Select(b => $"{b.Id}:{b.Title}:{b.Intent}:{string.Join(",", b.Citations.Select(c => c.File + "@" + c.Lines))}:{string.Join(",", b.RelatedBlockIds)}"))
            + "//" + string.Join(",", r.Relations.Select(x => $"{x.From}>{x.To}:{x.Kind}"))
            + "//" + string.Join(",", r.InteractionPoints.Select(p => string.Join("+", p.BlockIds)));
    }

    [Fact]
    public void NoBlock_EqualsAWholeNonTrivialFile()
    {
        var result = Run();
        var lengths = GoldenDiff().Files.ToDictionary(f => f.Path, f => f.NewText!.Split('\n').Length);

        foreach (var block in result.Blocks)
        {
            var file = block.Citations[0].File;
            if (lengths[file] <= 30)
                continue; // trivial files are allowed to be one block
            var covered = block.Citations.Sum(c => { var (s, e) = ParseRange(c.Lines); return e - s + 1; });
            Assert.True(covered < lengths[file], $"block {block.Id} covers the whole file {file}");
        }
    }

    // ---- helpers ----

    private static StructuralBlock ByTitle(PipelineResult result, string fragment) =>
        result.Blocks.Single(b => b.Title.Contains(fragment, StringComparison.Ordinal));

    private static (int Start, int End) ParseRange(string lines)
    {
        var dash = lines.IndexOf('-');
        return dash < 0
            ? (int.Parse(lines), int.Parse(lines))
            : (int.Parse(lines[..dash]), int.Parse(lines[(dash + 1)..]));
    }
}
