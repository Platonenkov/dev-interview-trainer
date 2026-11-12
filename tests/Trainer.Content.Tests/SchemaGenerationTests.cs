using System.Text.Json.Nodes;
using Trainer.Content.Schema;

namespace Trainer.Content.Tests;

public sealed class SchemaGenerationTests
{
    private const string UpdateVariable = "DIT_UPDATE_SCHEMAS";

    [Fact]
    public void GeneratedSchemas_MatchCommittedFiles()
    {
        string directory = Path.Combine(Fixtures.RepositoryContent, "schema");
        IReadOnlyDictionary<string, string> generated = SchemaGenerator.Generate();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(directory);
            foreach ((string name, string text) in generated)
            {
                File.WriteAllText(Path.Combine(directory, name), text);
            }

            return;
        }

        foreach ((string name, string text) in generated)
        {
            string path = Path.Combine(directory, name);
            Assert.True(File.Exists(path), $"{name} is missing; run the tests with {UpdateVariable}=1 to generate it.");
            string committed = File.ReadAllText(path);
            Assert.True(committed == text, $"{name} is out of date; run the tests with {UpdateVariable}=1 to regenerate it.");
        }

        string[] extra = [.. Directory.EnumerateFiles(directory, "*.schema.json").Select(Path.GetFileName).OfType<string>().Except(generated.Keys, StringComparer.Ordinal)];
        Assert.Empty(extra);
    }

    [Fact]
    public void GeneratedQuestionSchema_ForbidsAdditionalPropertiesAndListsLevels()
    {
        JsonNode schema = JsonNode.Parse(SchemaGenerator.Generate()[SchemaGenerator.QuestionFile])!;

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        string[] levels = [.. schema["properties"]!["level"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())];
        Assert.Equal(["junior", "middle", "senior", "lead", "architect"], levels);
    }

    [Fact]
    public void GeneratedQuestionSchema_ListsTypesGradingAndNestedEnums()
    {
        JsonNode schema = JsonNode.Parse(SchemaGenerator.Generate()[SchemaGenerator.QuestionFile])!;
        JsonNode properties = schema["properties"]!;

        Assert.Equal(
            ["open", "single", "multi", "output", "find-bug", "situation"],
            properties["type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["self", "auto"], properties["grading"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotNull(properties["id"]!["pattern"]);
        Assert.NotNull(properties["slug"]!["pattern"]);
    }

    [Fact]
    public void GeneratedQuestionSchema_RequiresMandatoryFieldsOnly()
    {
        JsonNode schema = JsonNode.Parse(SchemaGenerator.Generate()[SchemaGenerator.QuestionFile])!;
        string[] required = [.. schema["required"]!.AsArray().Select(n => n!.GetValue<string>())];

        Assert.Contains("id", required);
        Assert.Contains("locales", required);
        Assert.DoesNotContain("code", required);
        Assert.DoesNotContain("expect", required);
    }
}
