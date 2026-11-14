using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trainer.TestSupport;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class ForwardedHeadersTests(PostgresFixture postgres)
{
    private const string RemoteHeader = "X-Test-Remote";

    // TestServer has no socket, so the peer address is injected from a header ahead of the whole app pipeline.
    private sealed class FakePeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteHeader, out Microsoft.Extensions.Primitives.StringValues value)
                    && IPAddress.TryParse(value.ToString(), out IPAddress? address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private Task<TrainerWebFactory> CreateAsync(params (string Key, string? Value)[] settings)
    {
        Dictionary<string, string?> map = [];
        foreach ((string key, string? value) in settings)
        {
            map[key] = value;
        }

        return TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Settings = map,
            ConfigureServices = services => services.AddSingleton<IStartupFilter, FakePeerStartupFilter>(),
        });
    }

    private static async Task<string> SwitchLanguageCookieAsync(TrainerWebFactory factory, string peer, string? forwardedProto)
    {
        // The assertions are about a plain-http request, so this client keeps the default http address.
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(HttpMethod.Get, "/lang/en?return=%2Fen");
        request.Headers.Add(RemoteHeader, peer);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return Assert.Single(response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task TrustedProxy_HttpsForwardedProto_MakesTheCookieSecure()
    {
        await using TrainerWebFactory factory = await CreateAsync(("App:ForwardedHeaders:KnownProxies:0", "203.0.113.7"));

        string cookie = await SwitchLanguageCookieAsync(factory, "203.0.113.7", "https");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustedNetwork_HttpsForwardedProto_MakesTheCookieSecure()
    {
        await using TrainerWebFactory factory = await CreateAsync(("App:ForwardedHeaders:KnownNetworks:0", "172.18.0.0/16"));

        string cookie = await SwitchLanguageCookieAsync(factory, "172.18.4.9", "https");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UntrustedPeer_ForwardedProtoIsIgnored()
    {
        await using TrainerWebFactory factory = await CreateAsync(
            ("App:ForwardedHeaders:KnownProxies:0", "203.0.113.7"), ("App:ForwardedHeaders:KnownNetworks:0", "172.18.0.0/16"));

        string cookie = await SwitchLanguageCookieAsync(factory, "198.51.100.9", "https");

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Loopback_IsTrustedByDefault()
    {
        await using TrainerWebFactory factory = await CreateAsync();

        Assert.Contains("secure", await SwitchLanguageCookieAsync(factory, "127.0.0.1", "https"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", await SwitchLanguageCookieAsync(factory, "198.51.100.9", "https"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustedProxy_WithoutForwardedProto_StaysPlainHttp()
    {
        await using TrainerWebFactory factory = await CreateAsync(("App:ForwardedHeaders:KnownProxies:0", "203.0.113.7"));

        string cookie = await SwitchLanguageCookieAsync(factory, "203.0.113.7", forwardedProto: null);

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // Host filtering runs before UseForwardedHeaders, so only what a handler sees can prove that the forwarded host is ignored.
    [Fact]
    public async Task ForwardedHost_NeverReplacesTheRealHost_ButProtoAndClientAddressAreApplied()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
            ForwardedHeadersSetup.Apply(options, ForwardedHeadersSetup.Parse(new ForwardedHeadersSettings { KnownProxies = ["203.0.113.7"] })));
        await using WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            await next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/echo", (HttpContext context) =>
            $"{context.Request.Scheme}|{context.Request.Host}|{context.Connection.RemoteIpAddress}");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/echo");
        request.Headers.Add("X-Forwarded-Host", "evil.test");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "198.51.100.20");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("https|localhost|198.51.100.20", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidNetwork_FailsStartupWithAClearMessage()
    {
        await using TrainerWebFactory factory = await CreateAsync(("App:ForwardedHeaders:KnownNetworks:0", "not-a-cidr"));

        Exception ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("App:ForwardedHeaders:KnownNetworks:0", Flatten(ex), StringComparison.Ordinal);
    }

    private static string Flatten(Exception ex)
    {
        List<string> messages = [];
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    [Fact]
    public void Parse_AcceptsNetworksAndProxiesIncludingIpv6()
    {
        TrustedProxies trusted = ForwardedHeadersSetup.Parse(new ForwardedHeadersSettings
        {
            KnownNetworks = ["10.0.0.0/8", " fd00::/8 "],
            KnownProxies = ["192.0.2.1", "2001:db8::1"],
        });

        Assert.Equal(2, trusted.Networks.Count);
        Assert.Equal(2, trusted.Proxies.Count);
    }

    [Theory]
    [InlineData("10.0.0.0/33", "KnownNetworks:0")]
    [InlineData("10.0.0.0", "KnownNetworks:0")]
    [InlineData("", "KnownNetworks:0")]
    public void Parse_RejectsInvalidNetworks(string value, string expectedKey)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ForwardedHeadersSetup.Parse(new ForwardedHeadersSettings { KnownNetworks = [value] }));

        Assert.Contains(expectedKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("300.1.1.1")]
    [InlineData("")]
    public void Parse_RejectsInvalidProxies(string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ForwardedHeadersSetup.Parse(new ForwardedHeadersSettings { KnownProxies = ["192.0.2.1", value] }));

        Assert.Contains("KnownProxies:1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AcceptsOnlyForAndProtoWithLimitOne()
    {
        ForwardedHeadersOptions options = new();

        ForwardedHeadersSetup.Apply(options, ForwardedHeadersSetup.Parse(new ForwardedHeadersSettings { KnownProxies = ["192.0.2.1"] }));

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPAddress.Parse("192.0.2.1"), options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
    }
}
