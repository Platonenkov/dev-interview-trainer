using System.Text.Json;
using System.Text.Json.Serialization;
using Trainer.Application.Mock;

namespace Trainer.Infrastructure.Mock;

/// <summary>
/// Shape of <c>mock_sessions.summary</c>: <c>{"topics":[{"topic","knew","partial","didntKnow","unanswered"}]}</c>.
/// The scalar parts of the summary are columns; only the per-topic list is JSON.
/// </summary>
internal sealed record MockSummaryDocument(IReadOnlyList<MockTopicDocument> Topics)
{
    public static string Serialize(IReadOnlyList<MockTopicResult> topics)
    {
        MockSummaryDocument document = new([.. topics.Select(
            t => new MockTopicDocument(t.Topic.Value, t.Knew, t.Partial, t.DidntKnow, t.Unanswered))]);
        return JsonSerializer.Serialize(document, MockSummaryJsonContext.Default.MockSummaryDocument);
    }

    /// <summary>
    /// Reads a stored summary; null when the text is not JSON or not an object of the expected shape, so one damaged row
    /// cannot fail a whole profile.
    /// </summary>
    public static MockSummaryDocument? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, MockSummaryJsonContext.Default.MockSummaryDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record MockTopicDocument(string Topic, int Knew, int Partial, int DidntKnow, int Unanswered);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MockSummaryDocument))]
internal sealed partial class MockSummaryJsonContext : JsonSerializerContext;
