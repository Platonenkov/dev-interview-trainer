using System.Text.Json.Serialization;
using Trainer.Content.Model;

namespace Trainer.Content.Reading;

/// <summary>
/// Strict deserialization of content documents: unknown members, missing required members and nulls in
/// non-nullable members are all errors, so a typo in a YAML file cannot silently drop data.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(QuestionDocument))]
[JsonSerializable(typeof(IReadOnlyList<StackDocument>))]
[JsonSerializable(typeof(IReadOnlyList<TopicDocument>))]
[JsonSerializable(typeof(IReadOnlyList<RedirectDocument>))]
public sealed partial class ContentJsonContext : JsonSerializerContext
{
}
