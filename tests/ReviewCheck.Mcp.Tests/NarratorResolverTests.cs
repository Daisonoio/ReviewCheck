using ReviewCheck.Mcp;

namespace ReviewCheck.Mcp.Tests;

/// <summary>
/// The narrator decision tree (docs/25 §hosting-mode). Tested as a pure function so it needs no
/// live MCP server: a configured key wins; else host sampling when supported (hosting notice);
/// else the deterministic facts narrative (deliberate-choice notice). Forced facts = no notice.
/// </summary>
public sealed class NarratorResolverTests
{
    [Fact]
    public void Key_Wins_NoNotice()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: true, factsForced: false, samplingSupported: true);
        Assert.Equal(NarrationMode.KeyLlm, mode);
        Assert.Null(notice);
    }

    [Fact]
    public void NoKey_SamplingSupported_IsHostingMode_WithNotice()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: false, factsForced: false, samplingSupported: true);
        Assert.Equal(NarrationMode.HostSampling, mode);
        Assert.Equal(NarratorResolver.HostingNotice, notice);
    }

    [Fact]
    public void NoKey_NoSampling_IsFacts_WithDeliberateChoiceNotice()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: false, factsForced: false, samplingSupported: false);
        Assert.Equal(NarrationMode.Facts, mode);
        Assert.Equal(NarratorResolver.FactsNotice, notice);
        Assert.Contains("deliberate", notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactsForced_IsFacts_NoNotice_EvenWithKeyOrSampling()
    {
        var (mode, notice) = NarratorResolver.Decide(keyConfigured: true, factsForced: true, samplingSupported: true);
        Assert.Equal(NarrationMode.Facts, mode);
        Assert.Null(notice);
    }

    [Fact]
    public async Task Resolve_NullServer_NoKey_FallsBackToFacts()
    {
        // No live server (null) means sampling is not available → facts + notice.
        var (narrator, notice) = await new NarratorResolver(new HttpClient(), factsForced: false).ResolveAsync(null);
        Assert.NotNull(narrator);
        // With no key configured in the test environment, this is the facts fallback.
        if (!Llm.AnthropicByoProvider.IsConfigured)
            Assert.Equal(NarratorResolver.FactsNotice, notice);
    }

    [Fact]
    public async Task Notice_IsGuaranteed_InTheFirstBlocksUncertainty()
    {
        // The mode notice must be shown by construction, not left to the host: it rides the first
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

            Assert.Equal(NarratorResolver.FactsNotice, plan.Notice);
            Assert.Contains(NarratorResolver.FactsNotice, plan.FirstBlock.Explanation.Uncertainty!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
