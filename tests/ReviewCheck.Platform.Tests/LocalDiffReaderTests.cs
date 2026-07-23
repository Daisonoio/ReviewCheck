using System.Diagnostics;
using ReviewCheck.Platform;

namespace ReviewCheck.Platform.Tests;

/// <summary>
/// Integration tests against a REAL temporary git repository (local process, no network —
/// G-NOPHONE). Covers the three ref modes: working, staged, commit.
/// </summary>
public sealed class LocalDiffReaderTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "rc-git-" + Guid.NewGuid().ToString("N"));

    public LocalDiffReaderTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init -q");
        Git("config user.email t@t.local");
        Git("config user.name t");
        File.WriteAllText(Path.Combine(_repo, "Program.cs"),
            "class Program\n{\n    static void Main() { }\n}\n");
        Git("add .");
        Git("commit -q -m initial");
    }

    [Fact]
    public void Working_ReadsUncommittedChanges_WithFullNewText()
    {
        File.WriteAllText(Path.Combine(_repo, "Program.cs"),
            "class Program\n{\n    static void Main() { System.Console.WriteLine(\"hi\"); }\n}\n");

        var result = new LocalDiffReader(_repo).Read("working");

        var file = Assert.Single(result.Files);
        Assert.Equal("Program.cs", file.Path);
        Assert.Equal(FileChangeKind.Modified, file.Kind);
        Assert.NotEmpty(file.Hunks);
        Assert.Contains("WriteLine", file.NewText);
        // The changed line is line 3 in the new file.
        Assert.Contains(3, file.Hunks[0].AddedNewLines);
    }

    [Fact]
    public void Staged_ReadsIndexContent()
    {
        File.WriteAllText(Path.Combine(_repo, "New.cs"), "class New { }\n");
        Git("add New.cs");

        var result = new LocalDiffReader(_repo).Read("staged");

        var file = Assert.Single(result.Files);
        Assert.Equal(("New.cs", FileChangeKind.Added), (file.Path, file.Kind));
        Assert.Equal("class New { }\n", file.NewText);
    }

    [Fact]
    public void Commit_ReadsThatCommitsChanges()
    {
        File.WriteAllText(Path.Combine(_repo, "Program.cs"),
            "class Program\n{\n    static void Main() { System.Console.WriteLine(1); }\n}\n");
        Git("commit -q -am change");

        var result = new LocalDiffReader(_repo).Read("HEAD");

        var file = Assert.Single(result.Files);
        Assert.Equal("Program.cs", file.Path);
        Assert.Contains("WriteLine(1)", file.NewText);
    }

    [Fact]
    public void NoChanges_YieldsEmptyResult()
    {
        var result = new LocalDiffReader(_repo).Read("working");
        Assert.Empty(result.Files);
    }

    [Fact]
    public void BadRef_ThrowsGitInvocationException_NotACrash()
    {
        Assert.Throws<GitInvocationException>(() => new LocalDiffReader(_repo).Read("no-such-ref"));
    }

    private void Git(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args}: {p.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch
        {
            // best-effort cleanup of the temp repo
        }
    }
}
