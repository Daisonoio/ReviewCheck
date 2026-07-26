using ReviewCheck.Mcp;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// The narrator decision tree (docs/25 §hosting-mode). Tested as a pure function so it needs no
/// live MCP server. Two visible behaviours: a grounded narrator (own key or host sampling) always
/// carries the 🟡 disclaimer; no/invalid key hands interpretation to the host with the 🔴 disclaimer.
/// Forced facts is the deterministic override — no LLM, no disclaimer.
/// </summary>
public sealed class NarratorResolverTests
{
    [Fact]
    public void Key_Wins_WithYellowDisclaimer()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: true, factsForced: false, samplingSupported: true);
        Assert.Equal(NarrationMode.KeyLlm, mode);
        Assert.Equal(NarratorResolver.GroundedDisclaimer, notice);
        Assert.Contains("🟡", notice!);
    }

    [Fact]
    public void NoKey_SamplingSupported_IsHostSampling_WithYellowDisclaimer()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: false, factsForced: false, samplingSupported: true);
        Assert.Equal(NarrationMode.HostSampling, mode);
        Assert.Equal(NarratorResolver.GroundedDisclaimer, notice);
        Assert.Contains("🟡", notice!);
    }

    [Fact]
    public void NoKey_NoSampling_HostInterprets_WithRedDisclaimer()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: false, factsForced: false, samplingSupported: false);
        Assert.Equal(NarrationMode.Facts, mode);
        Assert.Equal(NarratorResolver.HostInterpretDisclaimer, notice);
        Assert.Contains("🔴", notice!);
    }

    [Fact]
    public void FactsForced_IsFacts_NoDisclaimer_EvenWithKeyOrSampling()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: true, factsForced: true, samplingSupported: true);
        Assert.Equal(NarrationMode.Facts, mode);
        Assert.Null(notice);
    }

    [Fact]
    public async Task Resolve_NullServer_NoKey_HostInterprets()
    {
        // No live server (null) means sampling is not available → host interprets + red disclaimer.
        var (narrator, notice) = await new NarratorResolver(new HttpClient(), factsForced: false).ResolveAsync(null);
        Assert.NotNull(narrator);
        // With no key configured in the test environment, this is the host-interpretation fallback.
        if (!Llm.AnthropicByoProvider.IsConfigured)
            Assert.Equal(NarratorResolver.HostInterpretDisclaimer, notice);
    }

    [Fact]
    public async Task Disclaimer_IsGuaranteed_InTheFirstBlocksUncertainty()
    {
        // The disclaimer must be shown by construction, not left to the host: it rides the first
        // block's uncertainty (co-presence guarantee). Runs only when no key is configured.
        if (Llm.AnthropicByoProvider.IsConfigured)
            return;

        var root = Path.Combine(Path.GetTempPath(), "rc-notice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new ReviewEngine(
                new Provider.StubProvider(),
                new Session.SessionStore(root),
                new NarratorResolver(new HttpClient(), factsForced: false));

            var plan = await engine.GetReviewPlanAsync(new Core.Source.Local("working"));

            Assert.Equal(NarratorResolver.HostInterpretDisclaimer, plan.Notice);
            Assert.Contains(NarratorResolver.HostInterpretDisclaimer, plan.FirstBlock.Explanation.Uncertainty!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
