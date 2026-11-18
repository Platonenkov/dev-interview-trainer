using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

/// <summary>
/// The static edition is served under a path prefix (<c>/dev-interview-trainer/</c>), so a root-relative URL in a
/// stylesheet or script escapes the prefix and 404s there. These read the shipped assets.
/// </summary>
public sealed partial class StaticAssetsTests
{
    private static readonly string WwwRoot = Path.Combine(FindRepositoryRoot(), "src", "Trainer.Web", "wwwroot");

    [Fact]
    public void Css_UsesNoRootRelativeUrls()
    {
        string[] files = Directory.GetFiles(Path.Combine(WwwRoot, "css"), "*.css", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string css = File.ReadAllText(file);
            Assert.DoesNotMatch(RootRelativeUrl(), css);
        }

        // The fonts are the only url() the stylesheets use; they must still be there, relative to css/.
        Assert.Contains("url(\"../fonts/Inter-Regular.woff2\")", File.ReadAllText(Path.Combine(WwwRoot, "css", "site.css")), StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_UseNoRootRelativeUrlLiterals()
    {
        // Third-party code that is never edited here (its hash is pinned below); it carries no URL.
        string vendor = Path.Combine(WwwRoot, "js", "vendor") + Path.DirectorySeparatorChar;
        string[] files = [.. Directory.GetFiles(Path.Combine(WwwRoot, "js"), "*.js", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))];
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string code = LineComment().Replace(BlockComment().Replace(File.ReadAllText(file), ""), "");
            Assert.True(!RootRelativeLiteral().IsMatch(code), $"{Path.GetFileName(file)} has a root-relative URL literal: {RootRelativeLiteral().Match(code).Value}");
        }
    }

    [Theory]
    [InlineData("x('/_framework/a')", true)]
    [InlineData("x(\"/api/profile\")", true)]
    [InlineData("x(`/en`)", true)]
    [InlineData("x('/')", true)]
    [InlineData("x(\"/\")", true)]
    [InlineData("x('/2026/x')", true)]
    [InlineData("x('/-x')", true)]
    [InlineData("x('/.well-known/a')", true)]
    [InlineData("x('//cdn.example/a')", true)]
    [InlineData("x('_framework/')", false)]
    [InlineData("x('../fonts/a')", false)]
    [InlineData("x('a/b')", false)]
    [InlineData("x(/[?&]perf=1/)", false)]
    public void Scripts_RootRelativeDetection_CatchesEveryRootRelativeShape(string code, bool expected) =>
        Assert.Equal(expected, RootRelativeLiteral().IsMatch(code));

    [Fact]
    public void VendoredBrotliDecoder_IsTheFileRecordedInTheAdr()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(WwwRoot, "js", "vendor", "brotli-decode.mjs"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string adr = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "adr", "0027-brotli-boot-resources-on-pages.md"));

        Assert.Equal(1, Regex.Count(adr, hash, RegexOptions.CultureInvariant));
        Assert.StartsWith("/* Copyright 2017 Google Inc.", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("Distributed under MIT license.", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("export let BrotliDecode=", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_DefineThePageEditionBeforeTheRuntimeStarts()
    {
        string start = File.ReadAllText(Path.Combine(WwwRoot, "js", "islands-start.js"));
        int define = start.IndexOf("window.ditPageEdition =", StringComparison.Ordinal);
        int begin = start.IndexOf("Blazor.start(", StringComparison.Ordinal);
        Assert.True(define >= 0 && begin > define, "ditPageEdition must be defined before Blazor.start, which runs Program.Main.");

        // blazor.web.js must not start on its own, or Program.Main could run before islands-start.js defines the reader.
        string app = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Trainer.Web", "Components", "App.razor"));
        int framework = app.IndexOf("blazor.web.js", StringComparison.Ordinal);
        Assert.True(framework >= 0 && app.IndexOf("autostart=\"false\"", framework, StringComparison.Ordinal) > framework);
        Assert.True(app.IndexOf("islands-start.js", framework, StringComparison.Ordinal) > framework);
    }

    [Fact]
    public void ThemeScript_UsesTheNamespacedKey()
    {
        string toggle = File.ReadAllText(Path.Combine(WwwRoot, "js", "theme-toggle.js"));

        Assert.Contains("getItem('dit.theme')", ThemeScript.Source, StringComparison.Ordinal);
        Assert.Contains("getItem('dit.theme')", ThemeScript.StaticSource, StringComparison.Ordinal);
        Assert.DoesNotContain("getItem('theme')", ThemeScript.StaticSource, StringComparison.Ordinal);
        Assert.Contains("setItem('dit.theme'", toggle, StringComparison.Ordinal);
        // The old key is read once to carry an existing visitor's choice over; nothing writes it, and it is never removed
        // because the github.io origin is shared with other sites.
        Assert.DoesNotContain("setItem('theme'", ThemeScript.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("removeItem", ThemeScript.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("'theme'", toggle, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"url\(\s*[""']?/")]
    private static partial Regex RootRelativeUrl();

    // A literal that is "/" alone, or "/" followed by a name character, a digit, a hyphen, a dot or another slash.
    [GeneratedRegex(@"[""'`]/(?:[_A-Za-z0-9.\-/]|[""'`])")]
    private static partial Regex RootRelativeLiteral();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"(^|\s)//[^\r\n]*")]
    private static partial Regex LineComment();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("DevInterviewTrainer.slnx was not found above the test output directory.");
    }
}
