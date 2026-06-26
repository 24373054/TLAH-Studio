using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Tests;

public class AssistantContentFormatterTests
{
    [Fact]
    public void ComposeAndParse_PreservesThinkingAndAnswer()
    {
        var content = AssistantContentFormatter.Compose(
            "final answer",
            "reasoning line 1\nreasoning line 2",
            isThinkingExpanded: true);

        Assert.True(AssistantContentFormatter.TryParse(
            content,
            out var thinking,
            out var answer,
            out var expanded));
        Assert.True(expanded);
        Assert.Equal("reasoning line 1\nreasoning line 2", thinking);
        Assert.Equal("final answer", answer);
    }

    [Fact]
    public void StripThinking_ReturnsProviderSafeAnswerOnly()
    {
        var content = AssistantContentFormatter.Compose(
            "visible answer",
            "internal thinking",
            isThinkingExpanded: false);

        Assert.Equal("visible answer", AssistantContentFormatter.StripThinking(content));
    }

    [Fact]
    public void Preview_NormalizesWhitespaceAndTruncates()
    {
        var preview = AssistantContentFormatter.Preview("one\n two   three four", maxCharacters: 12);

        Assert.Equal("one two thre...", preview);
    }
}
