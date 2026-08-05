namespace ReviewCheck.Platform;

/// <summary>
/// Turns a remote GitHub pull request into the SAME <see cref="LocalDiffResult"/> shape
/// <c>AnalysisPipeline</c> already consumes for a local diff — same <see cref="UnifiedDiffParser"/>,
/// same Roslyn pipeline downstream, zero duplication. The only thing that differs from
/// <see cref="LocalDiffReader"/> is where the bytes come from: <see cref="IPullRequestPlatform"/>
/// instead of a local <c>git</c> process.
/// </summary>
public sealed class GitHubDiffReader(IPullRequestPlatform platform, string repo, string pr)
{
    public async Task<LocalDiffResult> ReadAsync(CancellationToken ct = default)
    {
        var headRef = await platform.GetHeadRefAsync(repo, pr, ct);
        var diffText = await platform.GetDiffAsync(repo, pr, ct);
        var files = UnifiedDiffParser.Parse(diffText).ToList();

        // Same reason LocalDiffReader loads NewText: the pipeline (Roslyn) needs the WHOLE file, not
        // just the changed hunks. A deleted file has no "new" content to fetch by definition.
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Kind == FileChangeKind.Deleted)
                continue;

            var content = await platform.GetFileContentAsync(repo, headRef, files[i].Path, ct);
            files[i] = files[i] with { NewText = content };
        }

        return new LocalDiffResult(pr, files);
    }
}
