using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Trainer.TestSupport;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class AllowedHostsTests(PostgresFixture postgres)
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Trainer.Web";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<HttpStatusCode> GetStatusAsync(TrainerWebFactory factory, string host)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Host = host;
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private Task<TrainerWebFactory> CreateProductionAsync(params (string Key, string? Value)[] settings)
    {
        Dictionary<string, string?> map = new() { ["App:PublicBaseUrl"] = "https://example.test" };
        foreach ((string key, string? value) in settings)
        {
            map[key] = value;
        }

        return TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Environment = "Production", Settings = map });
    }

    [Fact]
    public async Task Production_WithWildcardAllowedHosts_AcceptsOnlyThePublicHost()
    {
        await using TrainerWebFactory factory = await CreateProductionAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await GetStatusAsync(factory, "evil.test"));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(factory, "example.test"));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8080")]
    public async Task Production_WithWildcardAllowedHosts_StillAnswersLoopbackProbes(string host)
    {
        await using TrainerWebFactory factory = await CreateProductionAsync();

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(factory, host));
    }

    [Fact]
    public async Task Production_ExplicitAllowedHosts_Wins()
    {
        await using TrainerWebFactory factory = await CreateProductionAsync(("AllowedHosts", "other.test"));

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(factory, "other.test"));
        Assert.Equal(HttpStatusCode.BadRequest, await GetStatusAsync(factory, "example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, await GetStatusAsync(factory, "localhost"));
    }

    [Fact]
    public async Task Testing_KeepsTheWildcard()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(factory, "anything.test"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("")]
    [InlineData(null)]
    public void Apply_Production_ReplacesWildcardOrEmptyWithThePublicHost(string? configured)
    {
        ConfigurationManager configuration = new();
        configuration["AllowedHosts"] = configured;

        string? applied = AllowedHostsDefault.Apply(configuration, new AppOptions { PublicBaseUrl = "https://example.test:8443" }, new FakeEnvironment("Production"));

        Assert.Equal("example.test;localhost;127.0.0.1;[::1]", applied);
        Assert.Equal("example.test;localhost;127.0.0.1;[::1]", configuration["AllowedHosts"]);
    }

    [Theory]
    [InlineData("Development", "*")]
    [InlineData("Testing", "*")]
    [InlineData("Production", "a.test;b.test")]
    public void Apply_LeavesTheConfigurationAlone(string environment, string configured)
    {
        ConfigurationManager configuration = new();
        configuration["AllowedHosts"] = configured;

        string? applied = AllowedHostsDefault.Apply(configuration, new AppOptions { PublicBaseUrl = "https://example.test" }, new FakeEnvironment(environment));

        Assert.Null(applied);
        Assert.Equal(configured, configuration["AllowedHosts"]);
    }
}
