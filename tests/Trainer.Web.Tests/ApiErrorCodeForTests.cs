using Trainer.Contracts;
using Trainer.Web.Api;

namespace Trainer.Web.Tests;

public sealed class ApiErrorCodeForTests
{
    [Theory]
    [InlineData(401, ApiErrorCodes.SignInRequired)]
    [InlineData(403, ApiErrorCodes.Forbidden)]
    [InlineData(404, ApiErrorCodes.NotFound)]
    [InlineData(405, ApiErrorCodes.MethodNotAllowed)]
    [InlineData(413, ApiErrorCodes.PayloadTooLarge)]
    [InlineData(415, ApiErrorCodes.UnsupportedMediaType)]
    [InlineData(429, ApiErrorCodes.RateLimited)]
    [InlineData(500, ApiErrorCodes.ServerError)]
    [InlineData(503, ApiErrorCodes.ServerError)]
    [InlineData(400, ApiErrorCodes.Validation)]
    [InlineData(408, ApiErrorCodes.Validation)]
    [InlineData(411, ApiErrorCodes.Validation)]
    [InlineData(431, ApiErrorCodes.Validation)]
    public void CodeFor_MapsBodilessStatuses(int status, string expected) => Assert.Equal(expected, ApiErrorMiddleware.CodeFor(status));

    [Fact]
    public void CodeFor_BodilessConflict_IsNeverReportedAsOwnerMismatch() =>
        Assert.NotEqual(ApiErrorCodes.OwnerMismatch, ApiErrorMiddleware.CodeFor(409));
}
