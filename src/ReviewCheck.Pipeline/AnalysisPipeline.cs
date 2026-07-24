using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ReviewCheck.Core;
using ReviewCheck.Platform;

namespace ReviewCheck.Pipeline;

/// <summary>
/// The deterministic analysis pipeline (docs/24): diff → ordered structural blocks with
/// real line citations, graph relations, and seams. No LLM, no network, no randomness —
/// same diff in, same result out (golden-testable). Stages:
/// P1 hunk→symbol (Roslyn) · P2 dependency graph · P3 segmentation · P4 reading order ·
/// P5 citations · P6 relations+seams · P7 structural uncertainty · P8 graceful degradation.
/// </summary>
public sealed class AnalysisPipeline
{
    private const int CodeBudgetLines = 60;   // P3 size budget (SP3): beyond this, show hunks not the whole member
    private const int TrivialFileLines = 30;  // "no block = whole file" applies above this size
    private const int MaxUnresolvedListed = 5;

    public PipelineResult Run(LocalDiffResult diff)
    {
        // ---- P1 + P3 + P8: build candidate blocks per file ----
        var candidates = new List<Candidate>();
        var csharpFiles = new List<(FileDiff File, SyntaxTree Tree)>();

        foreach (var file in diff.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (file.Kind == FileChangeKind.Deleted || file.Hunks.All(h => h.NewRange is null))
                continue; // nothing readable on the new side

            if (IsCSharp(file.Path) && file.NewText is not null)
                csharpFiles.Add((file, CSharpSyntaxTree.ParseText(SourceText.From(file.NewText), path: file.Path)));
            else
                candidates.AddRange(FallbackBlocks(file)); // P8: never a crash
        }

        // One compilation over all changed C# files → real cross-file resolution (P2).
        var compilation = CSharpCompilation.Create(
            "reviewcheck-diff",
            csharpFiles.Select(t => t.Tree),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var (file, tree) in csharpFiles)
            candidates.AddRange(CSharpBlocks(file, tree, compilation));

        // ---- P2 + P7: dependency edges + unresolved symbols ----
        var edges = BuildEdges(candidates);

        // ---- P4: reading order (definitions before uses; stable tie-break) ----
        var ordered = Order(candidates, edges);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Id = $"b{i + 1}";

        // ---- P6: relations, related ids, seams (graph facts only — never invented) ----
        var relations = new List<BlockRelation>();
        var seams = new List<InteractionPoint>();
        foreach (var (user, used, kind) in edges.OrderBy(e => e.User.OrderKey, StringComparer.Ordinal)
                                                .ThenBy(e => e.Used.OrderKey, StringComparer.Ordinal))
        {
            relations.Add(new BlockRelation(user.Id, used.Id, kind));
            user.Related.Add(used.Id);
            used.Related.Add(user.Id);
            user.Facts.Add($"uses '{used.Title}'");
            used.Facts.Add($"used by '{user.Title}'");
            seams.Add(new InteractionPoint(
                $"'{user.Title}' uses what '{used.Title}' defines — verify the interaction.",
                [user.Id, used.Id]));
        }

        var blocks = ordered.Select(c => c.ToStructuralBlock()).ToList();
        return new PipelineResult(
            Title: $"Local changes ({diff.Ref}): {blocks.Count} block(s) across {diff.Files.Count} file(s)",
            Blocks: blocks,
            Relations: relations,
            InteractionPoints: seams);
    }

    // ================= C# path (P1, P3, P5, P7) =================

    private static IEnumerable<Candidate> CSharpBlocks(FileDiff file, SyntaxTree tree, CSharpCompilation compilation)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var text = tree.GetText();
        var fileLineCount = text.Lines.Count;
        var isTestFile = LooksLikeTest(file.Path, file.NewText!);

        // P1: map each hunk to the smallest enclosing "unit of intent" members it touches.
        var byMember = new Dictionary<MemberDeclarationSyntax, List<DiffHunk>>();
        var fileLevelHunks = new List<DiffHunk>();

        foreach (var hunk in file.Hunks.Where(h => h.NewRange is not null))
        {
            var members = root.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .Where(IsUnitMember)
                .Where(m => HunkTouchesMember(hunk, LineSpan(text, m)))
                .ToList();

            if (members.Count == 0)
            {
                fileLevelHunks.Add(hunk); // usings, top-level statements, namespace lines
                continue;
            }

            foreach (var m in members)
                (byMember.TryGetValue(m, out var list) ? list : byMember[m] = []).Add(hunk);
        }

        // P3: one changed symbol = one block.
        foreach (var (member, hunks) in byMember.OrderBy(kv => LineSpan(text, kv.Key).Start))
        {
            var span = LineSpan(text, member);
            var memberLines = span.End - span.Start + 1;

            // P5: deterministic citations = the hunk lines intersected with the member.
            var citations = hunks
                .Select(h => Clamp(h.NewRange!.Value, span))
                .Where(r => r is not null)
                .Select(r => new Citation(file.Path, FormatRange(r!.Value)))
                .Distinct()
                .ToList();
            if (citations.Count == 0)
                citations = [new Citation(file.Path, FormatRange(span))];

            // Invariant: no block = whole file (unless trivial). Budget: big member → hunk slices.
            var coversWholeFile = memberLines >= fileLineCount && fileLineCount > TrivialFileLines;
            var code = !coversWholeFile && memberLines <= CodeBudgetLines
                ? member.ToFullString().Trim('\n', '\r')
                : SliceLines(text, hunks, span);

            var (symbol, title, kindWord) = Describe(model, member, text, hunks);
            var facts = new List<string>
            {
                $"{kindWord} '{SymbolLabel(member)}' in {file.Path} (lines {FormatRange(span)})",
            };

            yield return new Candidate
            {
                File = file.Path,
                StartLine = span.Start,
                Title = title,
                Intent = isTestFile ? Intent.Test : InferMemberIntent(member, hunks, text),
                Code = code,
                Citations = citations,
                Facts = facts,
                Symbol = symbol,
                Node = member,
                Model = model,
                LineCount = memberLines,
            };
        }

        // File-level bucket (wiring/config-ish lines with no enclosing member).
        if (fileLevelHunks.Count > 0)
        {
            var citations = fileLevelHunks
                .Select(h => new Citation(file.Path, FormatRange(h.NewRange!.Value)))
                .Distinct()
                .ToList();

            yield return new Candidate
            {
                File = file.Path,
                StartLine = fileLevelHunks.Min(h => h.NewStart),
                Title = $"{FileName(file.Path)} — top-level changes",
                Intent = isTestFile ? Intent.Test : Intent.Wiring,
                Code = SliceLines(text, fileLevelHunks, (1, fileLineCount)),
                Citations = citations,
                Facts = [$"top-level lines changed in {file.Path}"],
                Node = root,
                Model = model,
                LineCount = fileLevelHunks.Sum(h => h.NewCount),
            };
        }
    }

    // ================= P8: graceful degradation =================

    private static IEnumerable<Candidate> FallbackBlocks(FileDiff file)
    {
        foreach (var hunk in file.Hunks.Where(h => h.NewRange is not null))
        {
            var range = hunk.NewRange!.Value;
            yield return new Candidate
            {
                File = file.Path,
                StartLine = range.Start,
                Title = $"{FileName(file.Path)} — change at lines {FormatRange(range)}",
                Intent = InferNonCSharpIntent(file.Path),
                Code = string.Join('\n', hunk.Lines.Where(l => l.NewNumber is not null).Select(l => l.Text)),
                Citations = [new Citation(file.Path, FormatRange(range))],
                Facts = [$"changed lines {FormatRange(range)} in {file.Path}"],
                Uncertainty = "Structural analysis unavailable for this file (non-C# or unparsed): " +
                              "citations come from the diff hunks only.",
                LineCount = hunk.NewCount,
            };
        }
    }

    // ================= P2 + P7: graph =================

    private static List<(Candidate User, Candidate Used, RelationKind Kind)> BuildEdges(List<Candidate> candidates)
    {
        // Symbol → candidate map (member symbols and their containing types).
        var bySymbol = new Dictionary<ISymbol, Candidate>(SymbolEqualityComparer.Default);
        foreach (var c in candidates.Where(c => c.Symbol is not null)
                                    .OrderBy(c => c.OrderKey, StringComparer.Ordinal))
        {
            bySymbol.TryAdd(c.Symbol!.OriginalDefinition, c);
            if (c.Symbol!.ContainingType is { } type)
                bySymbol.TryAdd(type.OriginalDefinition, c); // first member stands for its type
        }

        var edges = new List<(Candidate, Candidate, RelationKind)>();
        var seen = new HashSet<(string, string)>();

        foreach (var user in candidates.Where(c => c.Node is not null && c.Model is not null)
                                       .OrderBy(c => c.OrderKey, StringComparer.Ordinal))
        {
            var unresolved = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var name in user.Node!.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = user.Model!.GetSymbolInfo(name);
                var target = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

                if (target is null)
                {
                    // P7: not resolvable — declare, don't guess. (Skip trivia like 'var'.)
                    if (name.Identifier.Text is not ("var" or "nameof" or "dynamic"))
                        unresolved.Add(name.Identifier.Text);
                    continue;
                }

                var key = target.OriginalDefinition;
                var viaType = target.ContainingType?.OriginalDefinition;
                var used =
                    bySymbol.TryGetValue(key, out var direct) ? direct :
                    viaType is not null && bySymbol.TryGetValue(viaType, out var owner) ? owner : null;

                if (used is null || ReferenceEquals(used, user))
                    continue;

                if (seen.Add((user.OrderKey, used.OrderKey)))
                    edges.Add((user, used, user.Intent == Intent.Test ? RelationKind.Tests : RelationKind.Uses));
            }

            if (unresolved.Count > 0)
            {
                var listed = string.Join(", ", unresolved.Take(MaxUnresolvedListed).Select(n => $"'{n}'"));
                var suffix = unresolved.Count > MaxUnresolvedListed ? $" (+{unresolved.Count - MaxUnresolvedListed} more)" : "";
                user.Uncertainty = AppendSentence(user.Uncertainty,
                    $"Unresolved symbols: {listed}{suffix} — defined outside this diff; their behavior is not verified here.");
            }
        }

        return edges;
    }

    // ================= P4: reading order =================

    private static List<Candidate> Order(
        List<Candidate> candidates,
        List<(Candidate User, Candidate Used, RelationKind Kind)> edges)
    {
        // "Used" (definition) must precede "user": edge used → user in the topo graph.
        var successors = candidates.ToDictionary(c => c, _ => new List<Candidate>());
        var indegree = candidates.ToDictionary(c => c, _ => 0);
        foreach (var (user, used, _) in edges)
        {
            successors[used].Add(user);
            indegree[user]++;
        }

        var result = new List<Candidate>(candidates.Count);
        var ready = candidates.Where(c => indegree[c] == 0).ToList();

        while (ready.Count > 0)
        {
            // Deterministic tie-break: by file, then by line (docs/24 P4).
            ready.Sort((a, b) => string.CompareOrdinal(a.OrderKey, b.OrderKey));
            var next = ready[0];
            ready.RemoveAt(0);
            result.Add(next);

            foreach (var succ in successors[next])
                if (--indegree[succ] == 0)
                    ready.Add(succ);
        }

        // Cycles / leftovers: deterministic fallback by file/line — never dropped.
        foreach (var rest in candidates.Except(result).OrderBy(c => c.OrderKey, StringComparer.Ordinal))
            result.Add(rest);

        return result;
    }

    // ================= helpers =================

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTest(string path, string content)
    {
        // Match by test-file NAME convention, not any "Test" in the path — a production file under a
        // folder like "TestProject" or "TestUtilities" is not a test. The content attributes are the
        // strong signal and cover files named otherwise.
        var file = FileName(path);
        var isTestFileName =
            file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith("Spec.cs", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith("Specs.cs", StringComparison.OrdinalIgnoreCase);
        var hasTestAttribute =
            content.Contains("[Fact]") || content.Contains("[Theory]") ||
            content.Contains("[Test]") || content.Contains("[TestMethod]");
        return isTestFileName || hasTestAttribute;
    }

    private static bool IsUnitMember(MemberDeclarationSyntax m) => m is
        MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or
        FieldDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or
        DestructorDeclarationSyntax or EventDeclarationSyntax or IndexerDeclarationSyntax or
        DelegateDeclarationSyntax or EnumDeclarationSyntax;

    private static (int Start, int End) LineSpan(SourceText text, SyntaxNode node)
    {
        var span = node.Span;
        return (text.Lines.GetLineFromPosition(span.Start).LineNumber + 1,
                text.Lines.GetLineFromPosition(span.End).LineNumber + 1);
    }

    private static bool Intersects((int Start, int End) a, (int Start, int End) b) =>
        a.Start <= b.End && b.Start <= a.End;

    /// <summary>
    /// A hunk touches a member only if a line it actually CHANGED falls inside the member. The ±3
    /// context lines git prints around a change must not pull neighbouring members in (otherwise
    /// inserting one method flags its unchanged siblings as "modified"). Pure-deletion hunks (no
    /// added lines) fall back to range intersection so the removal still attributes to its member.
    /// </summary>
    private static bool HunkTouchesMember(DiffHunk hunk, (int Start, int End) memberSpan)
    {
        var changed = hunk.AddedNewLines.ToList();
        if (changed.Count > 0)
            return changed.Any(l => memberSpan.Start <= l && l <= memberSpan.End);
        return hunk.NewRange is { } r && Intersects(memberSpan, r);
    }

    private static (int Start, int End)? Clamp((int Start, int End) r, (int Start, int End) bounds)
    {
        var start = Math.Max(r.Start, bounds.Start);
        var end = Math.Min(r.End, bounds.End);
        return start <= end ? (start, end) : null;
    }

    private static string FormatRange((int Start, int End) r) =>
        r.Start == r.End ? $"{r.Start}" : $"{r.Start}-{r.End}";

    private static string FileName(string path) =>
        path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>Renders the changed lines (with 1 context line around each hunk) from the real file text.</summary>
    private static string SliceLines(SourceText text, IEnumerable<DiffHunk> hunks, (int Start, int End) bounds)
    {
        var wanted = new SortedSet<int>();
        foreach (var hunk in hunks.Where(h => h.NewRange is not null))
        {
            var clamped = Clamp(hunk.NewRange!.Value, bounds);
            if (clamped is null)
                continue;
            for (var l = Math.Max(1, clamped.Value.Start - 1);
                 l <= Math.Min(text.Lines.Count, clamped.Value.End + 1);
                 l++)
                wanted.Add(l);
        }

        var parts = new List<string>();
        int? prev = null;
        foreach (var line in wanted)
        {
            if (prev is not null && line > prev + 1)
                parts.Add("…");
            parts.Add(text.Lines[line - 1].ToString());
            prev = line;
        }
        return string.Join('\n', parts);
    }

    private static (ISymbol? Symbol, string Title, string KindWord) Describe(
        SemanticModel model, MemberDeclarationSyntax member, SourceText text, List<DiffHunk> hunks)
    {
        var symbol = member is FieldDeclarationSyntax field
            ? model.GetDeclaredSymbol(field.Declaration.Variables[0])
            : model.GetDeclaredSymbol(member);

        var kindWord = member switch
        {
            MethodDeclarationSyntax => "Method",
            ConstructorDeclarationSyntax => "Constructor",
            PropertyDeclarationSyntax => "Property",
            FieldDeclarationSyntax => "Field",
            EnumDeclarationSyntax => "Enum",
            DelegateDeclarationSyntax => "Delegate",
            _ => "Member",
        };

        var isNew = IsDeclarationLineAdded(member, text, hunks);
        var change = isNew ? "added" : "modified";
        return (symbol, $"{kindWord} {SymbolLabel(member)} — {change}", isNew ? "defines" : "modifies");
    }

    /// <summary>"TokenBucket.Allow" — speaking label from the declaration, no ids.</summary>
    private static string SymbolLabel(MemberDeclarationSyntax member)
    {
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text + " (ctor)",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables[0].Identifier.Text,
            EnumDeclarationSyntax e => e.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            _ => member.Kind().ToString(),
        };

        var type = member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type is null ? name : $"{type.Identifier.Text}.{name}";
    }

    private static bool IsDeclarationLineAdded(MemberDeclarationSyntax member, SourceText text, List<DiffHunk> hunks)
    {
        var declLine = text.Lines.GetLineFromPosition(member.SpanStart).LineNumber + 1;
        return hunks.Any(h => h.AddedNewLines.Contains(declLine));
    }

    private static Intent InferMemberIntent(MemberDeclarationSyntax member, List<DiffHunk> hunks, SourceText text) =>
        IsDeclarationLineAdded(member, text, hunks) ? Intent.Definition : Intent.Refactor;

    private static Intent InferNonCSharpIntent(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".json" or ".yml" or ".yaml" or ".xml" or ".config" or ".csproj" or ".props" or ".targets" or ".toml" or ".ini"
            ? Intent.Config
            : Intent.Other;
    }

    private static string? AppendSentence(string? existing, string sentence) =>
        existing is null ? sentence : $"{existing} {sentence}";

    /// <summary>Mutable working item; becomes an immutable StructuralBlock at the end.</summary>
    private sealed class Candidate
    {
        public required string File { get; init; }
        public required int StartLine { get; init; }
        public required string Title { get; init; }
        public required Intent Intent { get; init; }
        public required string Code { get; init; }
        public required List<Citation> Citations { get; init; }
        public required List<string> Facts { get; init; }
        public string? Uncertainty { get; set; }
        public ISymbol? Symbol { get; init; }
        public SyntaxNode? Node { get; init; }
        public SemanticModel? Model { get; init; }
        public int LineCount { get; init; }

        public string Id { get; set; } = "";
        public List<string> Related { get; } = [];

        /// <summary>Stable sort key: file, then zero-padded line (docs/24 P4 tie-break).</summary>
        public string OrderKey => $"{File}:{StartLine:D6}";

        public StructuralBlock ToStructuralBlock() => new(
            Id,
            Title,
            Intent,
            Code,
            Citations,
            Related.Distinct().ToList(),
            Facts,
            Uncertainty,
            EstimatedMinutes: 1 + LineCount / 25);
    }
}
