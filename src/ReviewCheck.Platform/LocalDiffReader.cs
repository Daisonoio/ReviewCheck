using System.Diagnostics;

namespace ReviewCheck.Platform;

/// <summary>
/// Mode A's diff source: local git, invoked as a process. No token, no network (G-NOPHONE).
/// Ref semantics (spec/mcp-tools.json → get_review_plan.source.ref):
/// 'working' (default) = uncommitted changes; 'staged' = the index; a range ('A...B') or a
/// single commit are passed to git as-is. After parsing, each file's full post-change
/// content (NewText) is loaded so the pipeline can parse it with Roslyn.
/// </summary>
public sealed class LocalDiffReader(string repoRoot) : IDiffReader
{
    public LocalDiffResult Read(string? @ref)
    {
        var effective = string.IsNullOrWhiteSpace(@ref) ? "working" : @ref.Trim();

        var args = effective switch
        {
            "working" => "diff --no-color --unified=3",
            "staged" => "diff --no-color --unified=3 --cached",
            _ when effective.Contains("..") => $"diff --no-color --unified=3 {effective}",
            _ => $"diff --no-color --unified=3 {effective}^!",
        };

        var diffText = RunGit(args);
        var files = UnifiedDiffParser.Parse(diffText)
            .Select(f => f with { NewText = LoadNewText(f, effective) })
            .ToList();

        return new LocalDiffResult(effective, files);
    }

    private string? LoadNewText(FileDiff file, string @ref)
    {
        if (file.Kind == FileChangeKind.Deleted)
            return null;

        try
        {
            switch (@ref)
            {
                case "working":
                {
                    var full = Path.Combine(repoRoot, file.Path);
                    return File.Exists(full) ? File.ReadAllText(full) : null;
                }
                case "staged":
                    return RunGit($"show :{file.Path}");
                default:
                {
                    // Range 'A...B' → content at B; single commit C → content at C.
                    var rev = @ref.Contains("..")
                        ? @ref[( @ref.LastIndexOf('.') + 1)..]
                        : @ref;
                    return RunGit($"show {rev}:{file.Path}");
                }
            }
        }
        catch (GitInvocationException)
        {
            return null; // unreadable content → the pipeline degrades gracefully (P8)
        }
    }

    /// <summary>Hard ceiling so a stuck git (pager, prompt, huge tree, wrong dir) can never hang the tool.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private string RunGit(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // git must never block waiting on stdin (pager/prompt)
            UseShellExecute = false,
        };
        // Neutralize interactive git: no pager, no credential/terminal prompts.
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
            ?? throw new GitInvocationException("git could not be started. Is git installed?");
        process.StandardInput.Close();

        // Read both streams concurrently: reading one to end while the other fills its pipe
        // buffer is the classic Process deadlock. Async reads drain both at once.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new GitInvocationException(
                $"git {arguments} timed out after {GitTimeout.TotalSeconds:0}s in '{repoRoot}'. " +
                "Is this a git repository? (Set REVIEWCHECK_REPO to the target repo path.)");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new GitInvocationException(
                $"git {arguments} failed (exit {process.ExitCode}) in '{repoRoot}': {stderr.Trim()}");

        return stdout;
    }
}

/// <summary>Thrown when the local git invocation fails (not a crash: callers degrade or report).</summary>
public sealed class GitInvocationException(string message) : Exception(message);
