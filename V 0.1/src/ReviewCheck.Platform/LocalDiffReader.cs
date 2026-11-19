using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReviewCheck.Platform;

/// <summary>
/// The diff source: local git, invoked as a process. No token, no network (G-NOPHONE).
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

            "working" => new[] { "diff", "--no-color", "--unified=3" },
            "staged" => new[] { "diff", "--no-color", "--unified=3", "--cached" },
            _ when effective.Contains("..") => DiffArgsFor(effective),
            _ => DiffArgsFor($"{effective}^!"),
        };

        var diffText = RunGit(args);
        var files = UnifiedDiffParser.Parse(diffText)
            .Select(f => f with { NewText = LoadNewText(f, effective) })
            .ToList();

        // `git diff` for the working tree lists only tracked modifications — brand-new files are
        // invisible to it. But reviewing AI-written code is mostly reviewing NEW files, so for the
        // 'working' ref we add the untracked (non-ignored) files as additions. (staged/range/commit
        // already include their new files via the diff itself.)
        if (effective == "working")
            files.AddRange(UntrackedAsAdded(files.Select(f => f.Path)));

        return new LocalDiffResult(effective, files);
    }


    private static string[] DiffArgsFor(string refExpr)
    {
        EnsureSafeRef(refExpr);
        return ["diff", "--no-color", "--unified=3", "--end-of-options", refExpr];
    }

    private static readonly Regex SafeRefPattern = new(@"^[A-Za-z0-9._/\-\^~:!]+$", RegexOptions.Compiled);

    private static void EnsureSafeRef(string refExpr)
    {
        if (refExpr.StartsWith('-'))
            throw new ArgumentException($"Invalid ref '{refExpr}': must not start with '-'.", nameof(refExpr));
        if (!SafeRefPattern.IsMatch(refExpr))
            throw new ArgumentException($"Invalid ref '{refExpr}': contains disallowed characters.", nameof(refExpr));
    }


    /// <summary>
    /// ReviewCheck's own session store lives under .reviewcheck/. Never surface our own artifacts
    /// in a review — regardless of whether the reviewed repo happens to gitignore that folder.
    /// </summary>
    private static bool IsReviewCheckArtifact(string path) =>
        path.StartsWith(".reviewcheck/", StringComparison.Ordinal) ||
        path.StartsWith(".reviewcheck\\", StringComparison.Ordinal);

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <see cref="repoRoot"/> and returns null if the
    /// result would land OUTSIDE it. Defense in depth: these paths come from git's own diff/ls-files
    /// output, which shouldn't contain "../" traversal under normal operation — but this file reads
    /// from disk using them, so it doesn't rely on that assumption holding.
    /// </summary>
    private string? ResolveUnderRoot(string relativePath)
    {
        var root = Path.GetFullPath(repoRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// Untracked, non-ignored files as synthetic "added" diffs (whole file = additions), so the
    /// pipeline sees new files exactly as if git had diffed them against nothing.
    /// </summary>
    private IEnumerable<FileDiff> UntrackedAsAdded(IEnumerable<string> alreadySeen)
    {
        var seen = alreadySeen.ToHashSet(StringComparer.Ordinal);

        // -z: NUL-separated, so paths with spaces/newlines stay intact. --exclude-standard honors .gitignore.
        var listing = RunGit(["ls-files", "--others", "--exclude-standard", "-z"]);
        foreach (var path in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Contains(path) || IsReviewCheckArtifact(path))
                continue;

            var full = ResolveUnderRoot(path);
            if (full is null)
                continue; // would escape repoRoot — never read outside it

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable (locked, gone, permissions) → skip, never crash
            }

            var lines = text.Split('\n');
            // Trailing newline yields an empty final element that isn't a real line.
            var lineCount = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
            if (lineCount == 0)
                continue; // empty new file: nothing to review

            var diffLines = new List<DiffLine>(lineCount);
            for (var i = 0; i < lineCount; i++)
                diffLines.Add(new DiffLine('+', lines[i].TrimEnd('\r'), null, i + 1));

            yield return new FileDiff(
                path,
                FileChangeKind.Added,
                [new DiffHunk(0, 0, 1, lineCount, diffLines)],
                NewText: text);
        }
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
                        var full = ResolveUnderRoot(file.Path);
                        return full is not null && File.Exists(full) ? File.ReadAllText(full) : null;
                    }
                case "staged":
                    return RunGit(["show", $":{file.Path}"]);
                default:
                    {
                        // Range 'A...B' → content at B; single commit C → content at C.
                        var rev = @ref.Contains("..")
                            ? @ref[(@ref.LastIndexOf('.') + 1)..]
                            : @ref;
                        // Belt-and-suspenders: `rev` was already validated in Read() via DiffArgsFor,
                        // but this call doesn't depend on that call order holding forever.
                        EnsureSafeRef(rev);
                        return RunGit(["show", "--end-of-options", $"{rev}:{file.Path}"]);
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

    private string RunGit(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // git must never block waiting on stdin (pager/prompt)
            UseShellExecute = false,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
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
        var argsForMessage = string.Join(' ', arguments);
        if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new GitInvocationException(
                $"git {argsForMessage} timed out after {GitTimeout.TotalSeconds:0}s in '{repoRoot}'. " +
                "Is this a git repository? (Set REVIEWCHECK_REPO to the target repo path.)");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new GitInvocationException(
                $"git {argsForMessage} failed (exit {process.ExitCode}) in '{repoRoot}': {stderr.Trim()}");

        return stdout;
    }
}

/// <summary>Thrown when the local git invocation fails (not a crash: callers degrade or report).</summary>
public sealed class GitInvocationException(string message) : Exception(message);
