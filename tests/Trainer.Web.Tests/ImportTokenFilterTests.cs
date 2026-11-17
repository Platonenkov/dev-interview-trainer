using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Trainer.Web.Internal;

namespace Trainer.Web.Tests;

public sealed class ImportTokenFilterTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static async Task<bool> PassesAsync(string? authorization, string? configured = Token)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(InternalOptions.TokenKey, configured)])
            .Build();
        DefaultHttpContext http = new();
        if (authorization is not null)
        {
            http.Request.Headers.Authorization = authorization;
        }

        bool nextCalled = false;
        await new ImportTokenFilter(configuration).InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>(null);
            });
        return nextCalled;
    }

    [Theory]
    [InlineData("Bearer " + Token, true)]
    [InlineData("bearer " + Token, true)]
    [InlineData("Bearer  " + Token, false)]
    [InlineData("Bearer " + Token + " ", false)]
    [InlineData("Bearer\t" + Token, false)]
    [InlineData("Basic " + Token, false)]
    [InlineData(Token, false)]
    [InlineData(null, false)]
    public async Task Filter_AcceptsOnlyTheExactToken(string? header, bool expected) =>
        Assert.Equal(expected, await PassesAsync(header));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Filter_UnconfiguredToken_RejectsEverything(string? configured) =>
        Assert.False(await PassesAsync("Bearer " + Token, configured));
}
