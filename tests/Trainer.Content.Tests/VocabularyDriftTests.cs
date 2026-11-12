using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Trainer.Content.Schema;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Content.Tests;

/// <summary>
/// The schema generator and the validator read their vocabularies and patterns from <see cref="ContentVocabulary"/> and the
/// Domain keys; these tests fail when the two sides stop agreeing.
/// </summary>
public sealed class VocabularyDriftTests
{
    private static readonly JsonNode QuestionSchema = JsonNode.Parse(SchemaGenerator.Generate()[SchemaGenerator.QuestionFile])!;

    // ECMA-262 treats a dollar as end of input; .NET also accepts a trailing newline, so the pattern is anchored with \z here.
    private static readonly Regex KebabVocabulary = new(ContentVocabulary.KebabPattern.Replace("$", @"\z", StringComparison.Ordinal), RegexOptions.CultureInvariant);

    private static readonly Regex UlidVocabulary = new(ContentVocabulary.UlidPattern.Replace("$", @"\z", StringComparison.Ordinal), RegexOptions.CultureInvariant);

    private static string[] SchemaEnum(string property) =>
        [.. QuestionSchema["properties"]![property]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())];

    [Fact]
    public void SchemaLevels_EqualDomainLevels() =>
        Assert.Equal(LevelKeys.All.Select(LevelKeys.ToKey), SchemaEnum("level"));

    [Fact]
    public void SchemaTypes_EqualDomainQuestionTypes() =>
        Assert.Equal(QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey), SchemaEnum("type"));

    [Fact]
    public void SchemaGradingModes_EqualDomainGradingModes() =>
        Assert.Equal(GradingModeKeys.All.Select(GradingModeKeys.ToKey), SchemaEnum("grading"));

    [Fact]
    public void SchemaLinkLanguages_EqualSupportedCultures()
    {
        JsonNode lang = QuestionSchema["properties"]!["links"]!["items"]!["properties"]!["lang"]!;

        Assert.Equal(CultureKey.Supported.Select(c => c.Value), lang["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void EveryLevelEnumMember_IsListedInLevelKeys() =>
        Assert.Equal(Enum.GetValues<Level>().OrderBy(l => (int)l), LevelKeys.All.OrderBy(l => (int)l));

    [Fact]
    public void EveryQuestionTypeEnumMember_IsListedInQuestionTypeKeys() =>
        Assert.Equal(Enum.GetValues<QuestionType>().OrderBy(t => (int)t), QuestionTypeKeys.All.OrderBy(t => (int)t));

    [Fact]
    public void EveryGradingModeEnumMember_IsListedInGradingModeKeys() =>
        Assert.Equal(Enum.GetValues<GradingMode>().OrderBy(g => (int)g), GradingModeKeys.All.OrderBy(g => (int)g));

    [Fact]
    public void KeyMaxLength_EqualsDomainMaxLength()
    {
        Assert.True(Slug.TryCreate(new string('a', ContentVocabulary.KeyMaxLength), out _));
        Assert.False(Slug.TryCreate(new string('a', ContentVocabulary.KeyMaxLength + 1), out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a-b")]
    [InlineData("a1-b2-c3")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("a_b")]
    [InlineData("a--b")]
    [InlineData("-a")]
    [InlineData("a-")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("é")]
    public void KebabPattern_AgreesWithDomainKeys(string value)
    {
        bool vocabulary = KebabVocabulary.IsMatch(value);

        Assert.Equal(vocabulary, Slug.TryCreate(value, out _));
        Assert.Equal(vocabulary, StackKey.TryCreate(value, out _));
        Assert.Equal(vocabulary, TopicKey.TryCreate(value, out _));
    }

    [Theory]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W")]
    [InlineData("71JD4X7Q8N9V2K3M4P5R6S7T8W")]
    [InlineData("81JD4X7Q8N9V2K3M4P5R6S7T8W")]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I")]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8U")]
    [InlineData("01jd4x7q8n9v2k3m4p5r6s7t8w")]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W\n")]
    [InlineData("short")]
    public void UlidPattern_AgreesWithQuestionId(string value) =>
        Assert.Equal(UlidVocabulary.IsMatch(value), QuestionId.TryCreate(value, out _));
}
