using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Trainer.TestSupport;
using Trainer.Web.Auth;

namespace Trainer.Web.Tests;

public sealed class AdminTelegramIdsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void AdminIds_ParseArrayAndCommaForms()
    {
        IConfiguration array = Config(("App:AdminTelegramIds:0", "11"), ("App:AdminTelegramIds:1", "22"));
        IConfiguration comma = Config(("App:AdminTelegramIds", "11, 22,11"));
        IConfiguration missing = Config();
        IConfiguration empty = Config(("App:AdminTelegramIds", ""));

        Assert.Equal([11L, 22L], AdminTelegramIds.Parse(array));
        Assert.Equal([11L, 22L], AdminTelegramIds.Parse(comma));
        Assert.Empty(AdminTelegramIds.Parse(missing));
        Assert.Empty(AdminTelegramIds.Parse(empty));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12a")]
    [InlineData("1,abc")]
    [InlineData("99999999999999999999")]
    public void AdminIds_InvalidEntry_ThrowsWithKey(string value)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => AdminTelegramIds.Parse(Config(("App:AdminTelegramIds", value))));

        Assert.Contains("App:AdminTelegramIds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminIds_InvalidArrayEntry_ThrowsWithKeyAndValue()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => AdminTelegramIds.Parse(Config(("App:AdminTelegramIds:0", "7"), ("App:AdminTelegramIds:1", "x7"))));

        Assert.Contains("App:AdminTelegramIds", error.Message, StringComparison.Ordinal);
        Assert.Contains("'x7'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("1000", null)]
    [InlineData(" ", " ")]
    public void StartupCheck_ProductionWithoutSecret_Throws(string? clientId, string? clientSecret)
    {
        IConfiguration configuration = Config(("TelegramOidc:ClientId", clientId), ("TelegramOidc:ClientSecret", clientSecret));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TelegramOidcStartupCheck.Validate(configuration, new Environment("Production")));

        Assert.Contains("TelegramOidc:ClientSecret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void StartupCheck_OutsideProduction_AllowsMissingCredentials(string environment)
    {
        TelegramOidcStartupCheck.Validate(Config(), new Environment(environment));
    }

    [Fact]
    public void StartupCheck_ProductionWithCredentials_Passes()
    {
        TelegramOidcStartupCheck.Validate(
            Config(("TelegramOidc:ClientId", "1000"), ("TelegramOidc:ClientSecret", "secret")),
            new Environment("Production"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void StartupCheck_MetadataAddressOutsideTesting_Throws(string environment)
    {
        IConfiguration configuration = Config(
            ("TelegramOidc:ClientId", "1000"),
            ("TelegramOidc:ClientSecret", "secret"),
            ("TelegramOidc:MetadataAddress", "http://127.0.0.1:5000/.well-known/openid-configuration"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TelegramOidcStartupCheck.Validate(configuration, new Environment(environment)));

        Assert.Contains("TelegramOidc:MetadataAddress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Appsettings_ContainNoClientSecret()
    {
        string web = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web");
        string[] files = Directory.GetFiles(web, "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            Assert.DoesNotContain("ClientSecret", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Trainer.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
