using System.Text.Json;
using System.Text.Json.Serialization;
using ReviewCheck.Core;

namespace ReviewCheck.Session;

/// <summary>
/// File-backed session store (docs/23 §4). One JSON file per session under
/// <c>.reviewcheck/</c> (gitignored). Gives resume for free (the G-ROUNDTRIP gate).
/// MVP-1: a plain file — permission hardening/cleanup/encryption is deferred (docs/22 §5).
/// </summary>
public sealed class SessionStore
{
    private readonly string _root;

    /// <param name="root">Directory for the state files. Defaults to <c>.reviewcheck</c> under the working directory.</param>
    public SessionStore(string? root = null) => _root = root ?? ".reviewcheck";

    /// <summary>Absolute-or-relative path of a session's state file.</summary>
    public string PathFor(string session) => Path.Combine(_root, $"session-{session}.json");

    /// <summary>Analyzes → new session: all blocks pending, current = first block. Writes the file, returns the id.</summary>
    public string Create(AnalyzedReview analyzed, Source source)
    {
        var id = "s-" + Guid.NewGuid().ToString("N")[..12];
        var blocks = analyzed.Blocks.Select((b, i) => BlockState.FromBlock(i, b)).ToList();

        var state = new SessionState(
            Session: id,
            Source: SourceState.FromSource(source),
            Title: analyzed.Title,
            Blocks: blocks,
            Progress: new ProgressState(
                CurrentBlockId: blocks.Count > 0 ? blocks[0].Id : null,
                CompletedBlockIds: []))
        {
            CreatedAt = DateTimeOffset.UtcNow,
            InteractionPoints = analyzed.InteractionPoints,
        };

        Save(state);
        return id;
    }

    public SessionState Load(string session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
            throw new SessionNotFoundException(session);

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<SessionState>(stream, SessionJson.Options)
            ?? throw new SessionNotFoundException(session);
    }

    public void Save(SessionState state)
    {
        Directory.CreateDirectory(_root);
        using var stream = File.Create(PathFor(state.Session));
        JsonSerializer.Serialize(stream, state, SessionJson.Options);
    }

    /// <summary>Records a human decision (accept / request_correction) and persists it.</summary>
    public void SetStatus(string session, string blockId, BlockStatus status, string? note = null)
    {
        var state = Load(session);
        var index = IndexOf(state, blockId);
        var blocks = state.Blocks.ToList();
        blocks[index] = blocks[index] with { Status = status, Note = note };
        Save(state with { Blocks = blocks });
    }

    /// <summary>
    /// Moves <c>current_block_id</c> to the next block by reading order and persists.
    /// Clamped at the last block. Returns the new current block (co-present) and its 1-based position.
    /// </summary>
    public (Block Block, Position Position) Advance(string session)
    {
        var state = Load(session);
        if (state.Blocks.Count == 0)
            throw new InvalidOperationException($"Session '{session}' has no blocks.");

        var currentId = state.Progress.CurrentBlockId ?? state.Blocks[0].Id;
        var currentIndex = IndexOf(state, currentId);
        var nextIndex = Math.Min(currentIndex + 1, state.Blocks.Count - 1);
        var next = state.Blocks[nextIndex];

        var completed = state.Progress.CompletedBlockIds.ToList();
        if (nextIndex != currentIndex && !completed.Contains(currentId))
            completed.Add(currentId);

        Save(state with
        {
            Progress = state.Progress with { CurrentBlockId = next.Id, CompletedBlockIds = completed },
        });

        return (next.ToBlock(), new Position(nextIndex + 1, state.Blocks.Count));
    }

    private static int IndexOf(SessionState state, string blockId)
    {
        for (var i = 0; i < state.Blocks.Count; i++)
            if (state.Blocks[i].Id == blockId)
                return i;
        throw new BlockNotFoundException(state.Session, blockId);
    }
}

/// <summary>Central JSON options: snake_case property + enum names, matching the schema exactly.</summary>
public static class SessionJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

/// <summary>Thrown when a session id has no state file.</summary>
public sealed class SessionNotFoundException(string session)
    : Exception($"Session '{session}' not found.");

/// <summary>Thrown when a block id is not part of a session.</summary>
public sealed class BlockNotFoundException(string session, string blockId)
    : Exception($"Block '{blockId}' not found in session '{session}'.");
