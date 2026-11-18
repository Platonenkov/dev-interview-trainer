using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>The static edition served under a path base, as GitHub Pages serves a project site.</summary>
public sealed class StaticSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string PathBase = "/dit";

    public const string Origin = "https://example.test";

    public TrainerWebFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = Origin + PathBase + "/",
                },
            });
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
