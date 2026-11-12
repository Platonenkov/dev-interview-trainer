using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>One fully imported site per test class, for tests that only read.</summary>
public sealed class WebSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public TrainerWebFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Factory = await TrainerWebFactory.CreateAsync(postgres);
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
