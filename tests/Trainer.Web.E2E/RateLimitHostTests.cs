using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.Web.Api;

namespace Trainer.Web.E2E;

/// <summary>The browser host inherits the raised test limits, so browser runs never hit the production rate limits.</summary>
[Trait("Category", "E2E")]
public sealed class RateLimitHostTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    [Fact]
    public void TestHosts_UseRaisedLimits()
    {
        RateLimitOptions limits = host.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.Equal(100_000, limits.WritesPerMinute);
        Assert.Equal(100_000, limits.ReadsPerMinute);
        Assert.Equal(100_000, limits.SearchPerMinute);
    }
}
