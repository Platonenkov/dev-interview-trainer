using System.Net;
using System.Net.Http.Headers;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class MaintenanceGateTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _flagDirectory = Path.Combine(Path.GetTempPath(), "dit-flags-" + Guid.NewGuid().ToString("N"));

    private string FlagPath => Path.Combine(_flagDirectory, "maintenance");

    public void Dispose()
    {
        if (Directory.Exists(_flagDirectory))
        {
            Directory.Delete(_flagDirectory, recursive: true);
        }
    }

    private async Task<TrainerWebFactory> CreateFactoryAsync(bool configured = true, int internalPort = 0)
    {
        Directory.CreateDirectory(_flagDirectory);
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                InternalPort = internalPort,
                Settings = configured ? new Dictionary<string, string?> { ["App:MaintenanceFlagPath"] = FlagPath } : null,
            });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private void RaiseFlag() => File.WriteAllText(FlagPath, string.Empty);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int? localPort = null, string? token = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (localPort is not null)
        {
            request.Headers.Add(LocalPortFromHeaderStartupFilter.Header, localPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertGateAnswerAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("60", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task MaintenanceGate_FlagPresent_RejectsPostPutDeleteWith503(string method)
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        foreach (string path in new[] { "/api/anything", "/en/dotnet", "/language" })
        {
            using HttpResponseMessage response = await SendAsync(client, new HttpMethod(method), path);

            await AssertGateAnswerAsync(response);
        }
    }

    [Fact]
    public async Task MaintenanceGate_FlagPresent_AllowsGet()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options })
        {
            using HttpResponseMessage response = await SendAsync(client, method, "/health/ready");

            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using HttpResponseMessage page = await SendAsync(client, HttpMethod.Get, "/en/dotnet");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task MaintenanceGate_FlagPresent_RejectsSignInCallbackGet()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage callback = await SendAsync(client, HttpMethod.Get, "/signin-telegram?code=x&state=y");
        using HttpResponseMessage challenge = await SendAsync(client, HttpMethod.Get, "/signin-telegram/challenge");

        await AssertGateAnswerAsync(callback);
        await AssertGateAnswerAsync(challenge);
    }

    [Fact]
    public async Task MaintenanceGate_FlagRemoved_WritesWorkWithinTwoSeconds()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();
        using (HttpResponseMessage blocked = await SendAsync(client, HttpMethod.Post, "/api/anything"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        }

        File.Delete(FlagPath);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        HttpStatusCode status;
        do
        {
            using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/anything");
            status = response.StatusCode;
            if (status != HttpStatusCode.ServiceUnavailable)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task MaintenanceGate_InternalPortExempt()
    {
        const int internalPort = 8081;
        await using TrainerWebFactory factory = await CreateFactoryAsync(internalPort: internalPort);
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage internalImport = await SendAsync(
            client, HttpMethod.Post, "/internal/content/import", internalPort, TrainerWebFactory.ImportToken);
        using HttpResponseMessage publicImport = await SendAsync(
            client, HttpMethod.Post, "/internal/content/import", 8080, TrainerWebFactory.ImportToken);

        Assert.Equal(HttpStatusCode.OK, internalImport.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, publicImport.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/anything")]
    [InlineData("POST", "/en/dotnet/questions/closure-for-loop")]
    [InlineData("GET", "/signin-telegram")]
    [InlineData("POST", "/internalx/content/import")]
    public async Task MaintenanceGate_InternalPort_GatesEveryPathOutsideTheInternalPrefix(string method, string path)
    {
        const int internalPort = 8081;
        await using TrainerWebFactory factory = await CreateFactoryAsync(internalPort: internalPort);
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await SendAsync(client, new HttpMethod(method), path, internalPort);

        await AssertGateAnswerAsync(response);
    }

    [Fact]
    public async Task MaintenanceGate_InternalPort_FormPostIsGated()
    {
        const int internalPort = 8081;
        await using TrainerWebFactory factory = await CreateFactoryAsync(internalPort: internalPort);
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/en/dotnet/questions/closure-for-loop")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["value"] = "like" }),
        };
        request.Headers.Add(LocalPortFromHeaderStartupFilter.Header, internalPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertGateAnswerAsync(response);
    }

    [Fact]
    public async Task MaintenanceGate_InternalPort_ReadsOutsideTheInternalPrefixStillPassTheGate()
    {
        const int internalPort = 8081;
        await using TrainerWebFactory factory = await CreateFactoryAsync(internalPort: internalPort);
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/en", internalPort);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task MaintenanceGate_Response_IsNoStoreAndCarriesSecurityHeaders()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/anything");

        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [Fact]
    public async Task MaintenanceGate_Unconfigured_IsOff()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync(configured: false);
        RaiseFlag();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/anything");

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
