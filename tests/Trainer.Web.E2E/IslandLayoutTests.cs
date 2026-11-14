namespace Trainer.Web.E2E;

/// <summary>
/// Spec 9.1 and 11.8 over every island page, sign-in and admin: at 375, 768, 1280 and 1920 px (dark) and 375 and
/// 1280 px (light), plus 320 px where the narrowest phones are most likely to break, each rendering is screenshotted to
/// <c>TestResults/screenshots/island-{page}-{width}-{theme}.png</c> and fails only on functional layout assertions.
/// Touch targets are also checked for a mouse, a touch screen, and a laptop with both.
/// </summary>
[Trait("Category", "E2E")]
[Collection(IslandSweepDefinition.Name)]
public sealed class IslandLayoutTests(IslandSweepFixture sweep)
{
    private static readonly int[] DarkWidths = [375, 768, 1280, 1920];
    private static readonly int[] LightWidths = [375, 1280];

    public static TheoryData<string, int, string> Layouts()
    {
        TheoryData<string, int, string> data = [];
        foreach (string page in IslandSweepFixture.Pages.Keys)
        {
            foreach (int width in DarkWidths)
            {
                data.Add(page, width, "dark");
            }

            foreach (int width in LightWidths)
            {
                data.Add(page, width, "light");
            }
        }

        data.Add("practice-feedback", 320, "dark");
        data.Add("profile", 320, "dark");
        data.Add("signin", 320, "dark");
        data.Add("search-429", 320, "dark");
        return data;
    }

    // The narrowest supported width in both cultures: Russian labels are longer, so the header and buttons wrap differently.
    public static TheoryData<string, string> Narrowest() => new()
    {
        { "practice-feedback", "en" },
        { "practice-feedback", "ru" },
        { "profile", "en" },
        { "profile", "ru" },
        { "signin", "en" },
        { "signin", "ru" },
        { "search-429", "en" },
        { "search-429", "ru" },
    };

    public static TheoryData<string, int, PointerKind> Pointers()
    {
        TheoryData<string, int, PointerKind> data = [];
        foreach (string page in IslandSweepFixture.Pages.Keys)
        {
            data.Add(page, 375, PointerKind.Coarse);
            data.Add(page, 768, PointerKind.Coarse);
            data.Add(page, 768, PointerKind.FineWithTouch);
            data.Add(page, 1280, PointerKind.Fine);
            data.Add(page, 1280, PointerKind.FineWithTouch);
            data.Add(page, 1920, PointerKind.Fine);
        }

        return data;
    }

    private static string Describe(string page, int width, string theme) => $"{page} at {width} px ({theme})";

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task NoHorizontalOverflow(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        Assert.True(
            probe.ScrollWidth <= probe.InnerWidth,
            $"{Describe(page, width, theme)}: scrollWidth {probe.ScrollWidth} > innerWidth {probe.InnerWidth}.");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task PrimaryActionVisible(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        Assert.True(probe.PrimaryProblem.Length == 0, $"{Describe(page, width, theme)}: {probe.PrimaryProblem}.");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task NoOverlappingKeyElements(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        Assert.True(probe.Overlaps.Count == 0, $"{Describe(page, width, theme)}: {string.Join("; ", probe.Overlaps)}.");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task HeaderNeverBreaksAWord(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        Assert.True(probe.BrokenWords.Count == 0, $"{Describe(page, width, theme)}: words broken across lines: {string.Join(", ", probe.BrokenWords)}.");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task CodeFontAtLeast13px(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        if (IslandSweepFixture.Pages[page].HasCode)
        {
            Assert.True(probe.CodeElements > 0, $"{Describe(page, width, theme)}: no code panel was rendered.");
        }

        Assert.True(
            probe.CodeElements == 0 || probe.MinCodeFontPx >= 13,
            $"{Describe(page, width, theme)}: code font {probe.MinCodeFontPx} px.");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task IslandPages_HaveNoCspViolations(string page, int width, string theme)
    {
        PageProbe probe = await sweep.ProbeAsync(ProbeKey.Natural(page, width, theme));

        Assert.True(probe.CspViolations.Count == 0, string.Join(" | ", probe.CspViolations));
    }

    [Theory]
    [MemberData(nameof(Narrowest))]
    public async Task At320px_FitsWithoutBreakingWordsInBothCultures(string page, string culture)
    {
        PageProbe probe = await sweep.ProbeAsync(new ProbeKey(page, 320, "dark", PointerKind.Coarse, culture));
        string what = $"{page} at 320 px ({culture})";

        Assert.True(probe.ScrollWidth <= probe.InnerWidth, $"{what}: scrollWidth {probe.ScrollWidth} > innerWidth {probe.InnerWidth}.");
        Assert.True(probe.BrokenWords.Count == 0, $"{what}: words broken across lines: {string.Join(", ", probe.BrokenWords)}.");
        Assert.True(probe.PrimaryProblem.Length == 0, $"{what}: {probe.PrimaryProblem}.");
        Assert.True(probe.Overlaps.Count == 0, $"{what}: {string.Join("; ", probe.Overlaps)}.");
        Assert.True(probe.UndersizedTargets.Count == 0, $"{what}: targets below {IslandSweepFixture.MinimumTarget} px: {string.Join("; ", probe.UndersizedTargets)}.");
    }

    [Theory]
    [MemberData(nameof(Pointers))]
    public async Task TouchTargetsAtLeast44px(string page, int width, PointerKind device)
    {
        PageProbe probe = await sweep.ProbeAsync(new ProbeKey(page, width, "dark", device, "en"));

        Assert.True(probe.Targets > 0, $"{page} at {width} px ({device}): no targets found.");
        Assert.True(
            probe.UndersizedTargets.Count == 0,
            $"{page} at {width} px ({device}): targets below {IslandSweepFixture.MinimumTarget} px: {string.Join("; ", probe.UndersizedTargets)}.");

        Assert.Equal(device != PointerKind.Coarse, probe.FinePointer);

        // Spec 9.1: shortcut hints only where the primary pointer is fine, which includes a laptop with a touch screen. The
        // question screens must render them (hidden or shown), so a page that lost its hints fails instead of matching 0 = 0.
        if (IslandSweepFixture.Pages[page].HasShortcutHints)
        {
            Assert.True(probe.KeyboardHints > 0, $"{page} at {width} px ({device}): no shortcut hints rendered.");
        }
        else
        {
            Assert.Equal(0, probe.KeyboardHints);
        }

        int expectedHints = device == PointerKind.Coarse ? 0 : probe.KeyboardHints;
        Assert.True(
            probe.KeyboardHintsShown == expectedHints,
            $"{page} at {width} px ({device}): {probe.KeyboardHintsShown} of {probe.KeyboardHints} shortcut hints shown, expected {expectedHints}.");
    }
}
