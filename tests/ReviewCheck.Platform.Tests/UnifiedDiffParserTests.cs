using ReviewCheck.Platform;

namespace ReviewCheck.Platform.Tests;

/// <summary>
/// Golden tests for the unified diff parser (docs/24: deterministic, same input → same output).
/// The sample is a fixed, realistic `git diff` covering: modification, addition, deletion,
/// rename, multiple hunks, and the "\ No newline" marker.
/// </summary>
public class UnifiedDiffParserTests
{
    private const string Sample =
        """
        diff --git a/src/Calc.cs b/src/Calc.cs
        index 3b18e51..b6fc4c6 100644
        --- a/src/Calc.cs
        +++ b/src/Calc.cs
        @@ -1,6 +1,8 @@
         public class Calc
         {
        -    public int Add(int a, int b) => a + b;
        +    public int Add(int a, int b) => checked(a + b);
        +
        +    public int Sub(int a, int b) => a - b;

             public int Mul(int a, int b) => a * b;
         }
        @@ -10,3 +12,4 @@ public class Calc
         // footer
         // end
         // of file
        +// new trailing comment
        diff --git a/README.md b/README.md
        new file mode 100644
        index 0000000..d95a1f0
        --- /dev/null
        +++ b/README.md
        @@ -0,0 +1,2 @@
        +# Title
        +Body
        \ No newline at end of file
        diff --git a/old.txt b/old.txt
        deleted file mode 100644
        index e69de29..0000000
        --- a/old.txt
        +++ /dev/null
        @@ -1,1 +0,0 @@
        -obsolete
        diff --git a/a/Renamed.cs b/b/NewName.cs
        similarity index 90%
        rename from Renamed.cs
        rename to NewName.cs
        index 111..222 100644
        --- a/Renamed.cs
        +++ b/NewName.cs
        @@ -3,1 +3,1 @@
        -old line
        +new line
        """;

    [Fact]
    public void Parses_AllFourFiles_WithKinds()
    {
        var files = UnifiedDiffParser.Parse(Sample);

        Assert.Equal(4, files.Count);
        Assert.Equal(("src/Calc.cs", FileChangeKind.Modified), (files[0].Path, files[0].Kind));
        Assert.Equal(("README.md", FileChangeKind.Added), (files[1].Path, files[1].Kind));
        Assert.Equal(("old.txt", FileChangeKind.Deleted), (files[2].Path, files[2].Kind));
        Assert.Equal(("NewName.cs", FileChangeKind.Renamed), (files[3].Path, files[3].Kind));
        Assert.Equal("Renamed.cs", files[3].OldPath);
    }

    [Fact]
    public void Hunks_HaveExactRanges_AndLineNumbers()
    {
        var calc = UnifiedDiffParser.Parse(Sample)[0];

        Assert.Equal(2, calc.Hunks.Count);
        var h1 = calc.Hunks[0];
        Assert.Equal((1, 6, 1, 8), (h1.OldStart, h1.OldCount, h1.NewStart, h1.NewCount));
        Assert.Equal((1, 8), h1.NewRange);

        // The added lines carry the correct 1-based NEW line numbers.
        Assert.Equal([3, 4, 5], h1.AddedNewLines.ToList());

        // Context lines carry both numbers.
        var first = h1.Lines[0];
        Assert.Equal((' ', 1, 1), (first.Kind, first.OldNumber, first.NewNumber));

        var h2 = calc.Hunks[1];
        Assert.Equal([15], h2.AddedNewLines.ToList());
    }

    [Fact]
    public void AddedFile_AllLinesAreAdditions_NoNewlineMarkerIgnored()
    {
        var readme = UnifiedDiffParser.Parse(Sample)[1];

        var hunk = Assert.Single(readme.Hunks);
        Assert.All(hunk.Lines, l => Assert.Equal('+', l.Kind));
        Assert.Equal(2, hunk.Lines.Count); // the "\ No newline" marker is not a line
        Assert.Equal([1, 2], hunk.AddedNewLines.ToList());
    }

    [Fact]
    public void DeletedFile_HasNoNewRange()
    {
        var old = UnifiedDiffParser.Parse(Sample)[2];
        Assert.Null(old.Hunks[0].NewRange);
    }

    [Fact]
    public void Deterministic_TwoRunsProduceIdenticalModel()
    {
        var a = UnifiedDiffParser.Parse(Sample);
        var b = UnifiedDiffParser.Parse(Sample);

        // Records hold lists (reference equality), so compare a stable projection.
        Assert.Equal(Project(a), Project(b));

        static string Project(IReadOnlyList<FileDiff> files) => string.Join("|",
            files.Select(f => $"{f.Path}:{f.Kind}:{string.Join(",", f.Hunks.Select(h => $"{h.NewStart}+{h.NewCount}({h.Lines.Count})"))}"));
    }

    [Fact]
    public void EmptyDiff_YieldsNoFiles()
    {
        Assert.Empty(UnifiedDiffParser.Parse(""));
        Assert.Empty(UnifiedDiffParser.Parse("   \n"));
    }
}
