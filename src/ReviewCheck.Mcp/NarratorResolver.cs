using ModelContextProtocol.Server;
using ReviewCheck.Llm;
using ReviewCheck.Mcp.Provider;

namespace ReviewCheck.Mcp;

/// <summary>How a block's explanation is produced for a given review.</summary>
public enum NarrationMode
{
    /// <summary>The user's own Anthropic key (BYO) — grounded.</summary>
    KeyLlm,
    /// <summary>The host model via MCP sampling — grounded, no key.</summary>
    HostSampling,
    /// <summary>Deterministic structural floor; the host model may interpret it — no dedicated key.</summary>
    Facts,
}

/// <summary>
/// Chooses the narrator PER REVIEW and returns a user-facing disclaimer. The choice can only be made
/// at request time because the host's <c>sampling</c> capability is known only after the MCP
/// handshake — so <c>get_review_plan</c> resolves it, not startup DI. Decision tree:
/// <list type="number">
///   <item>a configured (and accepted) key → <see cref="NarrationMode.KeyLlm"/> — grounded, 🟡 disclaimer;</item>
///   <item>no key + host supports sampling → <see cref="NarrationMode.HostSampling"/> — grounded, 🟡 disclaimer;</item>
///   <item>no/invalid key → <see cref="NarrationMode.Facts"/> — the host model interprets the code, 🔴 disclaimer.</item>
/// </list>
/// <c>REVIEWCHECK_NARRATOR=facts</c> forces the deterministic floor with no LLM at all (opt-in, no disclaimer).
/// </summary>
public sealed class NarratorResolver(HttpClient http, bool factsForced)
{
    /// <summary>Default used when no resolver is injected (tests, stub mode): always facts.</summary>
    public static NarratorResolver FactsOnly { get; } = new(new HttpClient(), factsForced: true);

    // 🟡 Grounded mode (own key or host sampling): the explanations are LLM-generated and checked
    // against the grounding rules, but an LLM can still be wrong — so warn, don't reassure.
    public const string GroundedDisclaimer =
        "🟡 LLMs can give incorrect guidance — review the proposed code carefully.";

    // 🔴 No (or rejected) key and no host sampling: there is no dedicated LLM, so the host model
    // interprets the code itself. That interpretation is not grounding-checked and may be wrong or
    // partial — a red, unmissable warning. Covers both "no key" and "key rejected" in one line.
    public const string HostInterpretDisclaimer =
        "🔴 No LLM API key (or the configured key was rejected): the code is being interpreted by your " +
        "host model, so the guidance may be wrong or partial — review the code carefully.";

    /// <summary>Pure decision (testable without an MCP server).</summary>
    public static (NarrationMode Mode, string? Notice) Decide(bool keyConfigured, bool factsForced, bool samplingSupported)
    {
        if (factsForced) return (NarrationMode.Facts, null);
        if (keyConfigured) return (NarrationMode.KeyLlm, GroundedDisclaimer);
        if (samplingSupported) return (NarrationMode.HostSampling, GroundedDisclaimer);
        return (NarrationMode.Facts, HostInterpretDisclaimer);
    }

    // Probe result cached for the process: null = not probed, true = usable, false = rejected.
    private bool? _keyUsable;

    // MCP9005: the MCP `sampling` capability is deprecated in the spec (2026-07-28, SEP-2577) but
    // still present and functional in this SDK. Hosting mode relies on it deliberately; if a future
    // SDK removes it, the decision tree degrades to facts (with its notice) automatically.
#pragma warning disable MCP9005
    public async Task<(IBlockNarrator Narrator, string? Notice)> ResolveAsync(McpServer? server, CancellationToken ct = default)
    {
        // A configured-but-rejected key is treated exactly like no key (the user's rule): only an
        // explicit 401/403 counts as rejected; a transient failure keeps the key. The red disclaimer
        // already says "or the configured key was rejected", so no separate prefix is needed.
        var keyUsable = !factsForced && AnthropicByoProvider.IsConfigured;
        if (keyUsable && !await KeyUsableAsync(ct))
            keyUsable = false;

        var samplingSupported = server?.ClientCapabilities?.Sampling is not null;
        LogClientCapabilitiesOnce(server, samplingSupported);
        var (mode, notice) = Decide(keyUsable, factsForced, samplingSupported);

        IBlockNarrator narrator = mode switch
        {
            NarrationMode.KeyLlm => new LlmAdapter(new AnthropicByoProvider(http)),
            NarrationMode.HostSampling => new LlmAdapter(new HostSamplingLlmProvider(server!.AsSamplingChatClient())),
            _ => new FactsNarrator(),
        };
        return (narrator, string.IsNullOrWhiteSpace(notice) ? null : notice);
    }

    // Diagnostic (stderr, once per process): the exact capabilities the host declared at the MCP
    // handshake — so whether the host supports `sampling` is observed, not inferred. Visible via
    // `claude --debug` in the reviewcheck server stderr.
    private static bool _capsLogged;
    private static void LogClientCapabilitiesOnce(McpServer? server, bool samplingSupported)
    {
        if (_capsLogged) return;
        _capsLogged = true;

        var caps = server?.ClientCapabilities;
        string raw;
        try { raw = caps is null ? "<null>" : System.Text.Json.JsonSerializer.Serialize(caps); }
        catch (Exception e) { raw = $"<unserializable: {e.Message}>"; }

        Console.Error.WriteLine(
            $"[reviewcheck] host sampling capability: {(samplingSupported ? "PRESENT" : "absent")} " +
            $"— client capabilities: {raw}");
    }
#pragma warning restore MCP9005

    private async Task<bool> KeyUsableAsync(CancellationToken ct)
    {
        if (_keyUsable is { } cached)
            return cached;
        var status = await new AnthropicByoProvider(http).ValidateKeyAsync(ct);
        var usable = status != AnthropicByoProvider.KeyStatus.Unauthorized; // transient/unknown keeps the key
        _keyUsable = usable;
        return usable;
    }
}
