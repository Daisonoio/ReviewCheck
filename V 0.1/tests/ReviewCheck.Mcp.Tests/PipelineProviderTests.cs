using ReviewCheck.Core;
using ReviewCheck.Llm;
using ReviewCheck.Mcp;
using ReviewCheck.Mcp.Provider;
using ReviewCheck.Pipeline;
using ReviewCheck.Platform;
using ReviewCheck.Session;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// Docs/24 §7 — the plug-in check: with the REAL provider (diff → pipeline → narrator),
/// the MVP-1 guarantees hold unchanged: every block passes BlockGuard, the engine flow
/// works end-to-end, the PR source stays out of scope. The MVP-1 tests become regression tests.
/// </summary>
public sealed class PipelineProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-pp-" + Guid.NewGuid().ToString("N"));

    /// <summary>In-memory diff: no git needed — the IDiffReader seam does its job.</summary>
    private sealed class FakeDiffReader : IDiffReader
    {
        public LocalDiffResult Read(string? @ref) => new(@ref ?? "working",
        [
            new FileDiff("src/Greeter.cs", FileChangeKind.Added,
                [new DiffHunk(1, 7, 1, 7, AddedLines(
                    "namespace App;",
                    "",
                    "public class Greeter",
                    "{",
                    "    public string Hello(string name) => $\"Hello {name}\";",
                    "}",
                    ""))],
                NewText: "namespace App;\n\npublic class Greeter\n{\n    public string Hello(string name) => $\"Hello {name}\";\n}\n"),
            new FileDiff("src/Program.cs", FileChangeKind.Added,
                [new DiffHunk(1, 3, 1, 3, AddedLines(
                    "using App;",
                    "",
                    "System.Console.WriteLine(new Greeter().Hello(\"world\"));"))],
                NewText: "using App;\n\nSystem.Console.WriteLine(new Greeter().Hello(\"world\"));\n"),
        ]);

        private static List<DiffLine> AddedLines(params string[] texts) =>
            texts.Select((t, i) => new DiffLine('+', t, null, i + 1)).ToList();
    }

    /// <summary>In-memory GitHub: no HTTP needed — the IPullRequestPlatform seam does its job.</summary>
    private sealed class FakePullRequestPlatform : IPullRequestPlatform
    {
        private const string GreeterDiff =
            """
            diff --git a/src/Greeter.cs b/src/Greeter.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/Greeter.cs
            @@ -0,0 +1,6 @@
            +namespace App;
            +
            +public class Greeter
            +{
            +    public string Hello(string name) => $"Hello {name}";
            +}
            """;

        private const string GreeterFullText =
            "namespace App;\n\npublic class Greeter\n{\n    public string Hello(string name) => $\"Hello {name}\";\n}\n";

        public Task<IReadOnlyList<PullRequestSummary>> ListAsync(string repo, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PullRequestSummary>>(
                [new PullRequestSummary("1", "Add Greeter", "bob", IsSelfReview: false)]);

        public Task<PullRequestSummary> GetSummaryAsync(string repo, string pr, CancellationToken ct = default) =>
            Task.FromResult(new PullRequestSummary(pr, "Add Greeter", "bob", IsSelfReview: false));

        public Task<string> GetDiffAsync(string repo, string pr, CancellationToken ct = default) =>
            Task.FromResult(GreeterDiff);

        public Task<string> GetHeadRefAsync(string repo, string pr, CancellationToken ct = default) =>
            Task.FromResult("deadbeef");

        public Task<string?> GetFileContentAsync(string repo, string @ref, string path, CancellationToken ct = default) =>
            Task.FromResult<string?>(GreeterFullText);

        public Task<string> GetAuthenticatedLoginAsync(CancellationToken ct = default) =>
            Task.FromResult("alice");

        public Task SubmitReviewAsync(
            string repo, string pr, PullRequestReviewEvent reviewEvent,
            IReadOnlyList<PullRequestComment> comments, string? body = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by PipelineProviderTests — that's ReviewEngine's job.");
    }

    // FactsNarrator keeps these tests deterministic; LlmAdapter is exercised in ReviewCheck.Llm.Tests.
    private PipelineProvider NewProvider() =>
        new(new FakeDiffReader(), new AnalysisPipeline(), new FakePullRequestPlatform());
    private static readonly IBlockNarrator Facts = new FactsNarrator();

    [Fact]
    public async Task RealBlocks_AllPassBlockGuard_CoPresentAndGrounded()
    {
        var review = await NewProvider().AnalyzeAsync(new Source.Local("working"), Facts);

        Assert.NotEmpty(review.Blocks);
        Assert.All(review.Blocks, b =>
        {
            Assert.True(BlockGuard.IsValid(b), $"block '{b.Title}' violates co-presence/grounding");
            Assert.NotNull(b.Explanation.Uncertainty); // MVP-2 honesty: no-LLM narrative is declared
        });
    }

    [Fact]
    public async Task Titles_AreSpeaking_NotRawIds()
    {
        var review = await NewProvider().AnalyzeAsync(new Source.Local("working"), Facts);

        Assert.Contains(review.Blocks, b => b.Title.Contains("Greeter.Hello"));
        Assert.All(review.Blocks, b => Assert.False(b.Title.StartsWith('b') && b.Title.Length <= 3,
            "titles must be human-speaking, not bare ids"));
    }

    [Fact]
    public async Task Engine_FullFlow_WorksWithRealBlocks()
    {
        var engine = new ReviewEngine(NewProvider(), new SessionStore(_root));

        var plan = await engine.GetReviewPlanAsync(new Source.Local("working"));
        Assert.False(string.IsNullOrWhiteSpace(plan.FirstBlock.Code));
        Assert.NotEmpty(plan.FirstBlock.Explanation.Citations);

        foreach (var b in plan.Blocks)
            engine.AcceptBlock(plan.Session, b.Id);

        var result = await engine.SubmitReviewAsync(plan.Session, confirm: false);
        Assert.Equal("ready_to_proceed", result.Outcome);
        Assert.False(result.Posted);
    }

    [Fact]
    public async Task DefinitionComesBeforeItsUse_InTheReadingOrder()
    {
        var review = await NewProvider().AnalyzeAsync(new Source.Local("working"), Facts);

        var definition = review.Blocks.Single(b => b.Title.Contains("Greeter.Hello"));
        var wiring = review.Blocks.Single(b => b.Title.Contains("Program.cs"));
        var order = review.Blocks.Select(b => b.Id).ToList();

        Assert.True(order.IndexOf(definition.Id) < order.IndexOf(wiring.Id));
    }

    [Fact]
    public async Task PullRequestSource_Github_ProducesRealBlocks_SameAsLocal()
    {
        var review = await NewProvider().AnalyzeAsync(new Source.PullRequest("github", "org/repo", "1"), Facts);

        Assert.NotEmpty(review.Blocks);
        Assert.All(review.Blocks, b => Assert.True(BlockGuard.IsValid(b)));
        Assert.Contains(review.Blocks, b => b.Title.Contains("Greeter.Hello"));
    }

    [Fact]
    public async Task PullRequestSource_TitleNamesThePr_NotLocalChanges()
    {
        var engine = new ReviewEngine(NewProvider(), new SessionStore(_root));

        var plan = await engine.GetReviewPlanAsync(new Source.PullRequest("github", "org/repo", "42"));

        Assert.Contains("Pull request #42", plan.Title);
        Assert.DoesNotContain("Local changes", plan.Title);
    }

    [Fact]
    public async Task PullRequestSource_UnsupportedPlatform_ThrowsClearly()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProvider().AnalyzeAsync(new Source.PullRequest("azure_devops", "org/repo", "1"), Facts));

        Assert.Contains("azure_devops", ex.Message);
        Assert.Contains("github", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
