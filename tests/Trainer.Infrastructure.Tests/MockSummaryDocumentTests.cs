using Trainer.Infrastructure.Mock;

namespace Trainer.Infrastructure.Tests;

public sealed class MockSummaryDocumentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"topics\":\"x\"}")]
    [InlineData("{\"topics\":[{\"topic\":\"async\",\"knew\":\"many\"}]}")]
    [InlineData("null")]
    public void Parse_DamagedOrForeignJson_ReturnsNull(string json) => Assert.Null(MockSummaryDocument.Parse(json));

    [Fact]
    public void Parse_ReadsWhatSerializeWrote()
    {
        MockSummaryDocument? parsed = MockSummaryDocument.Parse(
            "{\"topics\":[{\"topic\":\"async\",\"knew\":3,\"partial\":1,\"didntKnow\":2,\"unanswered\":0}]}");

        MockTopicDocument topic = Assert.Single(Assert.IsType<MockSummaryDocument>(parsed).Topics);
        Assert.Equal(new MockTopicDocument("async", 3, 1, 2, 0), topic);
    }
}
