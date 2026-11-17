using Microsoft.Extensions.Configuration;
using Trainer.Application.Feedback;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class FeedbackSetupTests
{
    private static IConfiguration Configuration(string? reportsPerDay)
    {
        Dictionary<string, string?> values = [];
        if (reportsPerDay is not null)
        {
            values["App:Feedback:ReportsPerDay"] = reportsPerDay;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Read_WithoutTheSection_UsesTheDefault() => Assert.Equal(10, FeedbackSetup.Read(Configuration(null)).ReportsPerDay);

    [Fact]
    public void Read_TakesTheConfiguredLimit() => Assert.Equal(3, FeedbackSetup.Read(Configuration("3")).ReportsPerDay);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Read_NonPositiveLimit_FailsStartup(string value)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => FeedbackSetup.Read(Configuration(value)));

        Assert.Contains(FeedbackOptions.SectionName + ":ReportsPerDay", exception.Message, StringComparison.Ordinal);
    }
}
