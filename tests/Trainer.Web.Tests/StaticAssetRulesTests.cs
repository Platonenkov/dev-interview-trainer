using System.Text.RegularExpressions;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

public sealed class StaticAssetRulesTests
{
    [Theory]
    [InlineData("_framework/dotnet.native.8j2kz4qmvx.wasm", true)]
    [InlineData("_framework/dotnet.native.8j2kz4qmvx.wasm.br", true)]
    [InlineData("_framework/en/Trainer.Web.Client.resources.ug5gr7ue0f.wasm", true)]
    [InlineData("css/site.we5d1p27ih.css", true)]
    [InlineData("fonts/Inter-Regular.s55nqzfd5b.woff2", true)]
    [InlineData("data/ru/q/01J.0123456789abcdef.json", true)]
    [InlineData("data/en/dotnet/index.0123456789abcdef.json", true)]
    [InlineData("_framework/blazor.web.js", false)]
    [InlineData("_framework/resource-collection.X029I.js", false)]
    [InlineData("css/site.css", false)]
    [InlineData("build.json", false)]
    [InlineData("immutable.json", false)]
    [InlineData("sw.js", false)]
    [InlineData("ru/dotnet/", false)]
    [InlineData("ru/dotnet/questions/index.html", false)]
    [InlineData("data/ru/q/01J.0123456789abcdeg.json", false)]
    [InlineData("data/ru/q/01J.0123456789abcde.json", false)]
    [InlineData("css/site.we5d1p27i.css", false)]
    [InlineData("css/site.we5d1p27ihx.css", false)]
    [InlineData("css/site.WE5D1P27IH.css", false)]
    [InlineData("css/site.we5d1p27ih.css\n", false)]
    [InlineData("", false)]
    public void IsEligible_OnlyContentAddressedPaths(string path, bool expected) =>
        Assert.Equal(expected, StaticAssetRules.IsEligible(path));

    // The pattern is the fingerprint alphabet, so any dotted word of 10 lowercase letters or digits matches. Eligibility is
    // sound only because the Task 16 export verifier fails the build for a published mutable file that matches: a new file
    // such as these two must be fingerprinted or renamed before it can be exported.
    [Theory]
    [InlineData("js/x.javascript.js")]
    [InlineData("css/print.stylesheet.css")]
    public void IsEligible_AlsoMatchesAnyDottedTenCharacterWord_SoTheExportVerifierKeepsItSound(string path) =>
        Assert.True(StaticAssetRules.IsEligible(path));

    [Theory]
    [InlineData("_framework/dotnet.native.8j2kz4qmvx.wasm", true)]
    [InlineData("data/ru/q/01J.0123456789abcdef.json", true)]
    [InlineData("build.json", false)]
    [InlineData("sw.js", false)]
    [InlineData("ru/dotnet/", false)]
    [InlineData("_framework/blazor.web.js", false)]
    public void JavaScriptRegex_IsTheSameRuleAsAnEcmaScriptRegex(string path, bool expected)
    {
        // The literal is what the worker runs; the same regex in .NET with ECMAScript semantics must agree with IsEligible.
        Regex regex = new(StaticAssetRules.JavaScriptRegex, RegexOptions.ECMAScript);
        Assert.Equal(expected, regex.IsMatch(path));
        Assert.Equal(StaticAssetRules.IsEligible(path), regex.IsMatch(path));
    }

    [Fact]
    public void JavaScriptRegex_IsAnchoredAndHasNoUnescapedSlash()
    {
        string source = StaticAssetRules.JavaScriptRegex;

        Assert.StartsWith("^", source, StringComparison.Ordinal);
        Assert.EndsWith("$", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<!\\)/", source);
    }
}
