using Trainer.Content.Rendering;

namespace Trainer.Content.Tests;

public sealed class SafeLinkPolicyTests
{
    [Theory]
    [InlineData("https://x.dev", true)]
    [InlineData("HTTPS://x.dev/a?b=c#d", true)]
    [InlineData("/ru/dotnet", true)]
    [InlineData("/", true)]
    [InlineData("//evil", false)]
    [InlineData("/\\evil", false)]
    [InlineData("http://x", false)]
    [InlineData("https://", false)]
    [InlineData("https:x", false)]
    [InlineData("JaVaScRiPt:x", false)]
    [InlineData("java\tscript:x", false)]
    [InlineData("java\nscript:x", false)]
    [InlineData(" javascript:x", false)]
    [InlineData("\u0001javascript:x", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("mailto:a@b.dev", false)]
    [InlineData("#anchor", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void Policy(string url, bool ok) => Assert.Equal(ok, SafeLinkPolicy.IsAllowed(url));

    [Theory]
    [InlineData("/%2Fevil.com")]
    [InlineData("/%5Cevil.com")]
    [InlineData("/&#47;evil.com")]
    [InlineData("/&sol;evil.com")]
    [InlineData("/%252Fevil.com")]
    [InlineData("https://a.dev/%0Ajavascript:x")]
    public void Policy_RejectsTargetsThatBecomeUnsafeAfterDecoding(string url) =>
        Assert.False(SafeLinkPolicy.IsAllowed(url));

    [Theory]
    [InlineData("https://x.dev", true)]
    [InlineData("/ru/dotnet", false)]
    [InlineData("http://x.dev", false)]
    [InlineData("//x.dev", false)]
    [InlineData("javascript:x", false)]
    public void PolicyAbsolute(string url, bool ok) => Assert.Equal(ok, SafeLinkPolicy.IsAllowedAbsolute(url));
}
