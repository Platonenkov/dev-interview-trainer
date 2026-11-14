using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class IslandStateTests
{
    [Theory]
    [InlineData(0, false, IslandStateKind.Offline)]
    [InlineData(429, false, IslandStateKind.RateLimited)]
    [InlineData(401, false, IslandStateKind.SignedOut)]
    [InlineData(409, false, IslandStateKind.AccountChanged, ApiErrorCodes.OwnerMismatch)]
    // A 409 that is not the owner check (a proxy, a future endpoint) is not an account change.
    [InlineData(409, false, IslandStateKind.ServerError, null)]
    [InlineData(409, false, IslandStateKind.ServerError, ApiErrorCodes.Validation)]
    [InlineData(500, false, IslandStateKind.ServerError)]
    [InlineData(502, false, IslandStateKind.ServerError)]
    [InlineData(400, false, IslandStateKind.ServerError)]
    [InlineData(403, false, IslandStateKind.ServerError)]
    [InlineData(200, true, IslandStateKind.Empty)]
    [InlineData(200, false, IslandStateKind.Ready)]
    // A failure is a failure even when the caller passes an empty session for it.
    [InlineData(0, true, IslandStateKind.Offline)]
    [InlineData(500, true, IslandStateKind.ServerError)]
    public void IslandState_FromResult(int status, bool sessionEmpty, IslandStateKind expected, string? errorCode = null)
    {
        ApiResult<SessionDto> result = new(status is >= 200 and < 300 ? new SessionDto([], 0) : null, status, errorCode);

        Assert.Equal(expected, IslandState.FromResult(result, sessionEmpty));
    }

    [Fact]
    public void IslandState_SuccessWithoutBody_IsServerError()
    {
        // A 200 whose body did not parse (a proxy's page) carries no session; it must not look like an empty pool.
        ApiResult<SessionDto> result = new(null, 200, null);

        Assert.Equal(IslandStateKind.ServerError, IslandState.FromResult(result, sessionEmpty: true));
    }
}
