using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class TrainerWebFactoryOptionsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Clients_DefaultToHttpsLocalhost_WithoutChangingClientOptions()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        Uri optionsAddress = factory.ClientOptions.BaseAddress;

        using HttpClient plain = factory.CreateClient();
        using HttpClient nonRedirecting = factory.CreateNonRedirectingClient();

        Assert.Equal(new Uri("https://localhost"), plain.BaseAddress);
        Assert.Equal(new Uri("https://localhost"), nonRedirecting.BaseAddress);
        Assert.Equal(optionsAddress, factory.ClientOptions.BaseAddress);
    }

    [Fact]
    public async Task DevelopmentHost_DoesNotLoadTheDevelopersUserSecrets()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Environment = "Development" });

        IConfigurationRoot configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        Assert.DoesNotContain(
            configuration.Providers,
            p => p is Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider json
                && string.Equals(json.Source.Path, "secrets.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Options_FlowTelegramTimeAndLogsIntoTheHost()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        TimeProvider time = new FixedTimeProvider();
        CapturingLoggerProvider logs = new();
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions { Telegram = telegram, Time = time, Logs = logs });

        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("1000", configuration["TelegramOidc:ClientId"]);
        Assert.False(string.IsNullOrEmpty(configuration["TelegramOidc:ClientSecret"]));
        Assert.Equal(telegram.MetadataAddress.ToString(), configuration["TelegramOidc:MetadataAddress"]);
        Assert.Same(time, factory.Services.GetRequiredService<TimeProvider>());
        ILogger probe = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Probe");
        probe.Log(LogLevel.Information, new EventId(1), "probe", null, static (state, _) => state);
        Assert.Contains(logs.Entries, entry => entry.Category == "Probe");
    }

    private sealed class FixedTimeProvider : TimeProvider;
}
