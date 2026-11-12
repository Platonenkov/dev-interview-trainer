using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Content.Schema;

/// <summary>
/// Produces JSON Schema documents for editors from the content model. The application never reads them;
/// semantic rules that a schema cannot express stay in the validator.
/// </summary>
public static class SchemaGenerator
{
    public const string QuestionFile = "question.schema.json";
    public const string TopicsFile = "topics.schema.json";
    public const string StacksFile = "stacks.schema.json";
    public const string RedirectsFile = "redirects.schema.json";

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        // Keeps regex characters such as plus readable instead of escaping them as +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns file name to schema text; text uses LF line endings and ends with a newline.</summary>
    public static IReadOnlyDictionary<string, string> Generate() =>
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [QuestionFile] = Render(ContentJsonContext.Default.QuestionDocument, "Question"),
            [TopicsFile] = Render(ContentJsonContext.Default.IReadOnlyListTopicDocument, "Topics"),
            [StacksFile] = Render(ContentJsonContext.Default.IReadOnlyListStackDocument, "Stacks"),
            [RedirectsFile] = Render(ContentJsonContext.Default.IReadOnlyListRedirectDocument, "Redirects"),
        };

    private static string Render(JsonTypeInfo typeInfo, string title)
    {
        JsonSchemaExporterOptions options = new()
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };

        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(typeInfo, options);
        if (schema is JsonObject root)
        {
            JsonObject ordered = new()
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["title"] = title,
            };
            foreach ((string name, JsonNode? value) in root.ToArray())
            {
                root.Remove(name);
                ordered[name] = value;
            }

            schema = ordered;
        }

        return schema.ToJsonString(OutputOptions) + "\n";
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject node)
        {
            return schema;
        }

        if (node.ContainsKey("properties"))
        {
            node["additionalProperties"] = false;
        }

        Type? owner = context.PropertyInfo?.DeclaringType;
        string? name = context.PropertyInfo?.Name;
        if (owner is null || name is null)
        {
            return node;
        }

        if (owner == typeof(QuestionDocument))
        {
            ApplyQuestionRules(name, node);
        }
        else if (owner == typeof(CodeBlock) && name == "harness")
        {
            SetEnum(node, ContentVocabulary.Harnesses);
        }
        else if (owner == typeof(Expectation) && name == "kind")
        {
            SetEnum(node, ContentVocabulary.ExpectKinds);
        }
        else if (owner == typeof(LinkDocument))
        {
            if (name == "kind")
            {
                SetEnum(node, ContentVocabulary.LinkKinds);
            }
            else if (name == "lang")
            {
                SetEnum(node, CultureKey.Supported.Select(c => c.Value));
            }
            else if (name == "url")
            {
                node["pattern"] = "^https://";
            }
        }
        else if ((owner == typeof(StackDocument) || owner == typeof(TopicDocument)) && name == "key")
        {
            SetKebab(node);
        }

        return node;
    }

    private static void ApplyQuestionRules(string name, JsonObject node)
    {
        switch (name)
        {
            case "id":
                node["pattern"] = ContentVocabulary.UlidPattern;
                break;
            case "slug" or "stack" or "topic":
                SetKebab(node);
                break;
            case "level":
                SetEnum(node, LevelKeys.All.Select(LevelKeys.ToKey));
                break;
            case "type":
                SetEnum(node, QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey));
                break;
            case "grading":
                SetEnum(node, GradingModeKeys.All.Select(GradingModeKeys.ToKey));
                break;
            case "tags":
                if (node["items"] is JsonObject items)
                {
                    SetKebab(items);
                }

                break;
        }
    }

    private static void SetKebab(JsonObject node)
    {
        node["pattern"] = ContentVocabulary.KebabPattern;
        node["maxLength"] = ContentVocabulary.KeyMaxLength;
    }

    private static void SetEnum(JsonObject node, IEnumerable<string> values) =>
        node["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
