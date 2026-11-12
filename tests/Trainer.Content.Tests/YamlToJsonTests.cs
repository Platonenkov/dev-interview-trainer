using System.Text.Json.Nodes;
using Trainer.Content.Reading;
using Trainer.Content.Validation;
using YamlDotNet.RepresentationModel;

namespace Trainer.Content.Tests;

public sealed class YamlToJsonTests
{
    private static JsonNode Convert(string yaml) => Convert(yaml, out IReadOnlyList<ContentIssue> issues)
        ?? throw new InvalidOperationException(string.Join("; ", issues.Select(i => i.Message)));

    private static JsonNode? Convert(string yaml, out IReadOnlyList<ContentIssue> issues)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return YamlToJson.Convert(stream, out issues, "x.yaml");
    }

    [Theory]
    [InlineData("v: 012", "012")]
    [InlineData("v: '10'", "10")]
    [InlineData("v: \"10\"", "10")]
    [InlineData("v: 2026-11-11", "2026-11-11")]
    [InlineData("v: 1.5", "1.5")]
    [InlineData("v: 10abc", "10abc")]
    [InlineData("v: '~'", "~")]
    [InlineData("v: 'true'", "true")]
    public void PlainAndQuotedScalars_BecomeStrings(string yaml, string expected) =>
        Assert.Equal(expected, Convert(yaml)["v"]!.GetValue<string>());

    [Theory]
    [InlineData("v: 10", 10)]
    [InlineData("v: -3", -3)]
    [InlineData("v: 0", 0)]
    public void PlainIntegers_BecomeNumbers(string yaml, int expected) =>
        Assert.Equal(expected, Convert(yaml)["v"]!.GetValue<int>());

    [Theory]
    [InlineData("v: true", true)]
    [InlineData("v: false", false)]
    public void PlainBooleans_BecomeBooleans(string yaml, bool expected) =>
        Assert.Equal(expected, Convert(yaml)["v"]!.GetValue<bool>());

    [Theory]
    [InlineData("v: null")]
    [InlineData("v: ~")]
    [InlineData("v:")]
    public void NullForms_BecomeNull(string yaml) => Assert.Null(Convert(yaml)["v"]);

    [Fact]
    public void BlockScalar_BecomesString() =>
        Assert.Equal("line1\nline2\n", Convert("v: |\n  line1\n  line2\n")["v"]!.GetValue<string>());

    [Fact]
    public void SequencesAndMappings_ArePreserved()
    {
        JsonNode node = Convert("a: [1, two]\nb:\n  c: true\n");

        Assert.Equal(1, node["a"]![0]!.GetValue<int>());
        Assert.Equal("two", node["a"]![1]!.GetValue<string>());
        Assert.True(node["b"]!["c"]!.GetValue<bool>());
    }

    [Fact]
    public void Anchor_IsRejected()
    {
        Assert.Null(Convert("a: &x [1]\nb: *x\n", out IReadOnlyList<ContentIssue> issues));
        Assert.Contains(issues, i => i.Code == "yaml-alias-forbidden");
    }

    [Fact]
    public void MergeKey_IsRejected()
    {
        Assert.Null(Convert("base:\n  a: 1\nderived:\n  <<: {a: 2}\n", out IReadOnlyList<ContentIssue> issues));
        Assert.Contains(issues, i => i.Code == "yaml-alias-forbidden");
    }

    [Fact]
    public void MultipleDocuments_AreRejected()
    {
        Assert.Null(Convert("a: 1\n---\nb: 2\n", out IReadOnlyList<ContentIssue> issues));
        Assert.Contains(issues, i => i.Code == "yaml-multiple-documents");
    }

    [Fact]
    public void ExplicitTag_IsRejected()
    {
        Assert.Null(Convert("v: !!int 5\n", out IReadOnlyList<ContentIssue> issues));
        Assert.Contains(issues, i => i.Code == "content-structure");
    }

    [Fact]
    public void NonScalarKey_IsRejected()
    {
        Assert.Null(Convert("? [a, b]\n: 1\n", out IReadOnlyList<ContentIssue> issues));
        Assert.Contains(issues, i => i.Code == "content-structure");
    }
}
