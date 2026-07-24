using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>Docs/25 T2 gate: the fake returns its scripted outputs, in order, and records calls.</summary>
public sealed class FakeLlmProviderTests
{
    [Fact]
    public async Task ScriptedOutputs_ComeBackInOrder()
    {
        var fake = new FakeLlmProvider().Returns("first").Returns("second");

        Assert.Equal("first", await fake.CompleteAsync("s1", "u1"));
        Assert.Equal("second", await fake.CompleteAsync("s2", "u2"));
        Assert.Equal([("s1", "u1"), ("s2", "u2")], fake.Calls);
    }

    [Fact]
    public async Task ScriptedFailure_ThrowsLlmUnavailable()
    {
        var fake = new FakeLlmProvider().Fails("scripted outage");

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => fake.CompleteAsync("s", "u"));
        Assert.Equal("scripted outage", ex.Message);
    }

    [Fact]
    public async Task ExhaustedScript_FailsTheTest_NotSilently()
    {
        var fake = new FakeLlmProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.CompleteAsync("s", "u"));
    }
}
