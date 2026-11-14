using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// WCAG 2.2 AA checks over the island pages, sign-in and admin: every control has an accessible name in both cultures,
/// a page speaks one language, keyboard focus is always visible, and the verdict is announced exactly once.
/// </summary>
[Trait("Category", "E2E")]
[Collection(IslandSweepDefinition.Name)]
public sealed partial class IslandAccessibilityTests(IslandSweepFixture sweep)
{
    private const string LiveRegions = "[role=status], [role=alert], [role=log], [aria-live]:not([aria-live=off]), output";

    public static TheoryData<string, string> Cultures()
    {
        TheoryData<string, string> data = [];
        foreach (string page in IslandSweepFixture.Pages.Keys)
        {
            data.Add(page, "en");
            data.Add(page, "ru");
        }

        return data;
    }

    private Task<PageProbe> ProbeAsync(string page, string culture) =>
        sweep.ProbeAsync(new ProbeKey(page, 1280, "dark", PointerKind.Fine, culture));

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task EveryControlHasAnAccessibleName(string page, string culture)
    {
        PageProbe probe = await ProbeAsync(page, culture);

        int minimum = IslandSweepFixture.Pages[page].MinimumControls;
        Assert.True(probe.Controls >= minimum, $"{page} ({culture}): only {probe.Controls} controls in the accessibility tree, expected at least {minimum}.");
        Assert.True(probe.UnnamedControls.Count == 0, $"{page} ({culture}): controls without a name: {string.Join("; ", probe.UnnamedControls)}.");
    }

    // Resource keys, not text matching: each check names a string the page must show in its own culture, from the server's
    // resources for the shared chrome and from the client's for the island, so a culture mismatch between the two shows.
    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task IslandPages_SpeakOneLanguage(string page, string culture)
    {
        PageProbe probe = await ProbeAsync(page, culture);
        string other = culture == "ru" ? "en" : "ru";

        Assert.Equal(culture, probe.DocumentLanguage);
        if (probe.IslandCulture is { } islandCulture)
        {
            Assert.Equal(culture, islandCulture);
        }

        foreach ((LanguageCheck check, string? actual) in probe.LanguageValues)
        {
            string expected = IslandSweepFixture.Resource(check.ClientResource, culture, check.Key);
            Assert.NotEqual(IslandSweepFixture.Resource(check.ClientResource, other, check.Key), expected);
            Assert.True(actual is not null, $"{page} ({culture}): {check.Selector} is missing.");
            Assert.True(
                Normalize(actual).Contains(expected, StringComparison.Ordinal),
                $"{page} ({culture}): {check.Selector} {check.Attribute ?? "text"} is '{Normalize(actual)}', expected {check.Key} '{expected}'.");
        }
    }

    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static TheoryData<string, string> FocusPages()
    {
        TheoryData<string, string> data = [];
        foreach (string page in IslandSweepFixture.Pages.Keys)
        {
            data.Add(page, "dark");
        }

        data.Add("practice", "light");
        return data;
    }

    [Theory]
    [MemberData(nameof(FocusPages))]
    public async Task FocusIsVisible(string page, string theme)
    {
        await using IBrowserContext context = await sweep.NewContextAsync(1280, theme, PointerKind.Fine, "en");
        int minimum = Math.Min(5, IslandSweepFixture.Pages[page].MinimumControls);
        List<string> problems;
        IPage tab;
        if (page == "practice")
        {
            // The question and then its feedback: answering replaces most of the controls.
            tab = await sweep.Host.NewPageAsync(context);
            JsonElement session = await IslandSweepFixture.OpenPracticeQuestionAsync(tab, "en");
            problems = await TabThroughAsync(tab, "question", minimum);
            await IslandSweepFixture.AnswerWronglyAsync(tab, session);
            problems.AddRange(await TabThroughAsync(tab, "feedback", minimum));
        }
        else
        {
            tab = await sweep.OpenAsync(context, page, "en");
            problems = await TabThroughAsync(tab, page, minimum);
        }

        Assert.True(problems.Count == 0, $"{page} ({theme}): focus without the focus ring: {string.Join("; ", problems)}.");
        sweep.Host.AssertNoCspViolations(tab);
    }

    // Tabs until focus comes back to an element already visited in this pass. Each focused element must show the
    // project's focus ring (site.css :focus-visible: a solid outline of at least 2 px in --accent), drawn on the element
    // or, for a visually hidden radio or checkbox, on its label (.opt--pick:has(input:focus-visible)). A box-shadow does
    // not count: some controls carry one permanently (the Mock jump strip's current-question underline).
    private static async Task<List<string>> TabThroughAsync(IPage tab, string pass, int minimum)
    {
        List<string> problems = [];
        int focused = 0;
        for (int i = 0; i < 150; i++)
        {
            await tab.Keyboard.PressAsync("Tab");
            string result = await tab.EvaluateAsync<string>(
                """
                (pass) => {
                  const e = document.activeElement;
                  if (!e || e === document.body) return 'BODY';
                  if (e.getAttribute('data-focus-pass') === pass) return 'END';
                  e.setAttribute('data-focus-pass', pass);
                  const shown = el => {
                    const r = el.getBoundingClientRect();
                    const s = getComputedStyle(el);
                    return r.width > 2 && r.height > 2 && s.visibility !== 'hidden' && s.opacity !== '0';
                  };
                  const probe = document.createElement('span');
                  probe.style.color = 'var(--accent)';
                  document.body.append(probe);
                  const accent = getComputedStyle(probe).color;
                  probe.remove();
                  const ring = el => {
                    const s = getComputedStyle(el);
                    return s.outlineStyle === 'solid' && parseFloat(s.outlineWidth) >= 2 && s.outlineColor === accent;
                  };
                  if (!e.matches(':focus-visible')) return `${e.tagName.toLowerCase()}.${e.className} does not match :focus-visible`;
                  const ok = [e, e.closest('label')].some(c => c && shown(c) && ring(c));
                  return ok ? 'OK' : `${e.tagName.toLowerCase()}.${e.className} "${(e.textContent || e.getAttribute('aria-label') || '').trim().slice(0, 30)}"`;
                }
                """,
                pass);
            if (result == "END")
            {
                break;
            }

            if (result != "BODY")
            {
                focused++;
                if (result != "OK")
                {
                    problems.Add($"{pass}: {result}");
                }
            }
        }

        Assert.True(focused >= minimum, $"{pass}: only {focused} elements took focus, expected at least {minimum}.");
        return problems;
    }

    [Fact]
    public async Task VerdictAnnouncedOnce()
    {
        await using IBrowserContext context = await sweep.NewContextAsync(1280, "dark", PointerKind.Fine, "en");
        IPage tab = await sweep.Host.NewPageAsync(context);
        JsonElement session = await IslandSweepFixture.OpenPracticeQuestionAsync(tab, "en");
        string verdict = IslandSweepFixture.Resource(true, "en", "Feedback.Verdict.didnt-know");

        // Marks the live regions present before the answer and logs every text any live region takes from now on.
        await tab.EvaluateAsync(
            """
            (live) => {
              document.querySelectorAll(live).forEach(e => e.setAttribute('data-live-before', e.textContent.trim()));
              window.__liveLog = [];
              new MutationObserver(() => {
                for (const e of document.querySelectorAll(live)) {
                  const text = e.textContent.trim();
                  if (text && e.__lastText !== text) window.__liveLog.push(text);
                  e.__lastText = text;
                }
              }).observe(document.body, { subtree: true, childList: true, characterData: true });
            }
            """,
            LiveRegions);

        await IslandSweepFixture.AnswerWronglyAsync(tab, session);
        await tab.WaitForTimeoutAsync(1000);

        string[] announcing = await tab.EvaluateAsync<string[]>(
            "([live, verdict]) => [...document.querySelectorAll(live)].filter(e => e.textContent.includes(verdict)).map(e => `${e.getAttribute('role') ?? ''}|${e.getAttribute('aria-live') ?? ''}|${e.getAttribute('data-live-before') ?? 'new'}`)",
            new object[] { LiveRegions, verdict });
        string region = Assert.Single(announcing);
        // A polite status region that was already in the page, empty, before the answer: screen readers announce its change.
        Assert.Equal("status||", region);

        string[] log = await tab.EvaluateAsync<string[]>("() => window.__liveLog");
        Assert.Single(log, text => text.Contains(verdict, StringComparison.Ordinal));

        int feedbackLive = await tab.EvaluateAsync<int>(
            "(live) => { const f = document.querySelector('.feedback'); return (f.closest(live) ? 1 : 0) + f.querySelectorAll(live).length; }",
            LiveRegions);
        Assert.Equal(0, feedbackLive);
        sweep.Host.AssertNoCspViolations(tab);
    }
}
