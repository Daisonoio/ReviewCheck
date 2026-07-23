using ReviewCheck.Core;
using ReviewCheck.Session;

namespace ReviewCheck.Session.Tests;

/// <summary>
/// T4 exit check (docs/23 §6): Create → Load → Save reconstructs the state (round-trip),
/// decisions persist, and the pointer advances only through Advance.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-test-" + Guid.NewGuid().ToString("N"));

    private SessionStore NewStore() => new(_root);

    private static AnalyzedReview SampleReview() => new(
        Title: "Sample change",
        Blocks:
        [
            new Block("b1", "First", Intent.Definition, "code-1",
                new Explanation("what-1", "why-1", "link-1", [new Citation("A.cs", "1-3")], "unsure here"),
                RelatedBlockIds: ["b2"]),
            new Block("b2", "Second", Intent.Test, "code-2",
                new Explanation("what-2", "why-2", "link-2", [new Citation("B.cs", "7")]),
                RelatedBlockIds: ["b1"]),
        ],
        InteractionPoints: [new InteractionPoint("seam between b1 and b2", ["b1", "b2"])],
        EstimatedMinutes: 5);

    [Fact]
    public void Create_StartsAllPending_WithFirstBlockCurrent()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local());

        var state = store.Load(id);

        Assert.Equal("Sample change", state.Title);
        Assert.Equal(2, state.Blocks.Count);
        Assert.All(state.Blocks, b => Assert.Equal(BlockStatus.Pending, b.Status));
        Assert.Equal("b1", state.Progress.CurrentBlockId);
        Assert.Empty(state.Progress.CompletedBlockIds);
        Assert.Equal("local", state.Source.Type);
    }

    [Fact]
    public void Roundtrip_LoadThenSave_IsByteIdentical()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local("staged"));

        var afterCreate = File.ReadAllText(store.PathFor(id));
        store.Save(store.Load(id));
        var afterResave = File.ReadAllText(store.PathFor(id));

        Assert.Equal(afterCreate, afterResave);
    }

    [Fact]
    public void Roundtrip_PreservesGroundingAndUncertainty()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local());

        var block = store.Load(id).Blocks.Single(b => b.Id == "b1");

        Assert.Equal("unsure here", block.Explanation.Uncertainty);
        Assert.Single(block.Explanation.Citations);
        Assert.Equal("A.cs", block.Explanation.Citations[0].File);
        Assert.Equal("1-3", block.Explanation.Citations[0].Lines);
        Assert.True(BlockGuard.IsValid(block.ToBlock()));
    }

    [Fact]
    public void SetStatus_PersistsDecisionAndNote()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local());

        store.SetStatus(id, "b1", BlockStatus.Accepted);
        store.SetStatus(id, "b2", BlockStatus.CorrectionRequested, "rename this");

        var state = store.Load(id);
        Assert.Equal(BlockStatus.Accepted, state.Blocks.Single(b => b.Id == "b1").Status);
        var b2 = state.Blocks.Single(b => b.Id == "b2");
        Assert.Equal(BlockStatus.CorrectionRequested, b2.Status);
        Assert.Equal("rename this", b2.Note);
    }

    [Fact]
    public void Advance_MovesPointer_AndClampsAtLast()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local());

        var (block, position) = store.Advance(id);
        Assert.Equal("b2", block.Id);
        Assert.Equal(2, position.Index);
        Assert.Equal(2, position.Total);
        Assert.Equal("b2", store.Load(id).Progress.CurrentBlockId);
        Assert.Contains("b1", store.Load(id).Progress.CompletedBlockIds);

        // Clamped: advancing past the last block stays on the last.
        var (clamped, clampedPos) = store.Advance(id);
        Assert.Equal("b2", clamped.Id);
        Assert.Equal(2, clampedPos.Index);
    }

    [Fact]
    public void Load_UnknownSession_Throws()
    {
        Assert.Throws<SessionNotFoundException>(() => NewStore().Load("does-not-exist"));
    }

    [Fact]
    public void Persisted_Json_UsesSchemaSnakeCaseFieldNames()
    {
        var store = NewStore();
        var id = store.Create(SampleReview(), new Source.Local());

        var json = File.ReadAllText(store.PathFor(id));

        // Field names from session-state.schema.json (snake_case), not the C# PascalCase.
        Assert.Contains("\"order_index\"", json);
        Assert.Contains("\"current_block_id\"", json);
        Assert.Contains("\"completed_block_ids\"", json);
        Assert.Contains("\"related_block_ids\"", json);
        Assert.Contains("\"interaction_points\"", json);
        Assert.Contains("\"block_ids\"", json);
        // Enum values are snake_case too.
        Assert.Contains("\"definition\"", json);
        Assert.Contains("\"pending\"", json);
        Assert.DoesNotContain("\"OrderIndex\"", json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
