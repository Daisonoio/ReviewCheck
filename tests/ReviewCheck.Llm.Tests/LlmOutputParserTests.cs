using ReviewCheck.Llm;

namespace ReviewCheck.Llm.Tests;

/// <summary>Docs/25 T4: valid JSON → object; wrapped JSON → still parsed; broken JSON → handled error, never a crash.</summary>
public sealed class LlmOutputParserTests
{
    private const string Valid =
        """{"what": "Defines the method.", "why": "It is used by the wiring.", "link": "used by 'Program'", "uncertainty_semantic": null}""";

    [Fact]
    public void PlainJson_Parses()
    {
        Assert.True(LlmOutputParser.TryParse(Valid, out var e, out _));
        Assert.Equal("Defines the method.", e.What);
        Assert.Equal("It is used by the wiring.", e.Why);
        Assert.Equal("used by 'Program'", e.Link);
        Assert.Null(e.UncertaintySemantic);
    }

    [Fact]
    public void MarkdownFencedJson_Parses()
    {
        var fenced = $"```json\n{Valid}\n```";
        Assert.True(LlmOutputParser.TryParse(fenced, out var e, out _));
        Assert.Equal("Defines the method.", e.What);
    }

    [Fact]
    public void JsonWithProseAround_Parses()
    {
        var noisy = $"Here is the explanation you asked for:\n{Valid}\nHope that helps!";
        Assert.True(LlmOutputParser.TryParse(noisy, out var e, out _));
        Assert.Equal("Defines the method.", e.What);
    }

    [Fact]
    public void NoJsonAtAll_ReturnsFalse_WithReason()
    {
        Assert.False(LlmOutputParser.TryParse("I cannot answer that.", out _, out var error));
        Assert.Contains("no JSON", error);
    }

    [Fact]
    public void BrokenJson_ReturnsFalse_NotACrash()
    {
        Assert.False(LlmOutputParser.TryParse("""{"what": "unterminated""", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MissingFields_BecomeEmpty_ForTheRubricToReject()
    {
        Assert.True(LlmOutputParser.TryParse("""{"what": "only what present here"}""", out var e, out _));
        Assert.Equal("", e.Why);
        Assert.Equal("", e.Link);
    }

    [Fact]
    public void WhitespaceUncertainty_BecomesNull()
    {
        Assert.True(LlmOutputParser.TryParse(
            """{"what": "w", "why": "y", "link": "l", "uncertainty_semantic": "  "}""", out var e, out _));
        Assert.Null(e.UncertaintySemantic);
    }
}
