using Trainer.Web.Auth;

namespace Trainer.Web.Tests;

public sealed class TrainerCookieEventsTests
{
    [Theory]
    [InlineData("https://localhost/signin?ReturnUrl=%2Fen%2Fadmin", "/signin?ReturnUrl=%2Fen%2Fadmin")]
    [InlineData("http://localhost:5000/signin", "/signin")]
    [InlineData("/signin?ReturnUrl=%2Fen%2Fadmin", "/signin?ReturnUrl=%2Fen%2Fadmin")]
    [InlineData("/signin?denied=1&ReturnUrl=%2Fen", "/signin?denied=1&ReturnUrl=%2Fen")]
    [InlineData("file:///signin?x=1", "file:///signin?x=1")]
    public void SameSiteTarget_ReducesAbsoluteHttpUrisAndKeepsEverythingElse(string redirectUri, string expected) =>
        Assert.Equal(expected, TrainerCookieEvents.SameSiteTarget(redirectUri));
}
