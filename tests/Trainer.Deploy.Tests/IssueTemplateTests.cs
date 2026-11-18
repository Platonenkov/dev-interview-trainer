using Trainer.TestSupport;
using YamlDotNet.Serialization;

namespace Trainer.Deploy.Tests;

/// <summary>The issue form the static edition's "Report a problem" link prefills.</summary>
public sealed class IssueTemplateTests
{
    private static readonly string[] Reasons = ["Wrong answer", "Unclear wording", "Outdated", "Broken link", "Something else"];

    private static Dictionary<object, object> Load()
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), ".github", "ISSUE_TEMPLATE", "question-report.yml");
        return new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(path));
    }

    private static Dictionary<object, object> Field(List<object> body, string id) =>
        body.Cast<Dictionary<object, object>>().Single(f => f.TryGetValue("id", out object? v) && (string?)v == id);

    [Fact]
    public void QuestionReportTemplate_HasThePrefilledIdsTheLabelAndTheFiveReasons()
    {
        Dictionary<object, object> template = Load();

        Assert.Equal("[question] ", (string)template["title"]);
        Assert.Equal(["content-report"], ((List<object>)template["labels"]).Cast<string>());
        List<object> body = (List<object>)template["body"];
        foreach (string id in new[] { "question-id", "language", "page-url" })
        {
            Dictionary<object, object> field = Field(body, id);
            Assert.Equal("input", (string)field["type"]);
            Dictionary<object, object> validations = (Dictionary<object, object>)field["validations"];
            Assert.Equal("true", (string)validations["required"]);
        }

        Dictionary<object, object> reason = Field(body, "reason");
        Assert.Equal("dropdown", (string)reason["type"]);
        Dictionary<object, object> attributes = (Dictionary<object, object>)reason["attributes"];
        Assert.Equal(Reasons, ((List<object>)attributes["options"]).Cast<string>());
        Assert.Equal("textarea", (string)Field(body, "details")["type"]);
    }
}
