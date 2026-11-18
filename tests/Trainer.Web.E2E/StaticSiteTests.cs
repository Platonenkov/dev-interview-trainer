using System.Globalization;
using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// The exported site in a real browser under its path prefix: the language chooser, the islands running from browser
/// storage and the content files, the pages around them, and what the site must never do (a request outside the prefix, a
/// cookie). Service workers are blocked here so that a route or a request log shows what the page itself asked for.
/// </summary>
[Trait("Category", "E2E")]
public sealed class StaticSiteTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>, IAsyncLifetime
{
    private static float Wait => StaticSiteSteps.IslandTimeoutMs;

    public ValueTask InitializeAsync() => new(site.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<IBrowserContext> NewContextAsync(string locale = "en-US", bool mobile = false) =>
        site.NewContextAsync(locale, ServiceWorkerPolicy.Block, mobile);

    [Fact]
    public async Task RootChooser_SendsToTheStoredOrBrowserLanguage()
    {
        await using IBrowserContext stored = await NewContextAsync("en-US");
        await stored.AddInitScriptAsync("try { localStorage.setItem('dit.lang', 'ru'); } catch (e) { }");
        IPage storedTab = await site.NewPageAsync(stored);
        await storedTab.GotoAsync(site.BaseAddress.ToString());
        await storedTab.WaitForURLAsync(site.Url("ru/").ToString());
        Assert.Equal("ru", await storedTab.EvaluateAsync<string>("() => document.documentElement.lang"));

        await using IBrowserContext browser = await NewContextAsync("en-US");
        IPage browserTab = await site.NewPageAsync(browser);
        await browserTab.GotoAsync(site.BaseAddress.ToString());
        await browserTab.WaitForURLAsync(site.Url("en/").ToString());
        Assert.Equal("en", await browserTab.EvaluateAsync<string>("() => document.documentElement.lang"));

        await using IBrowserContext russian = await NewContextAsync("ru-RU");
        IPage russianTab = await site.NewPageAsync(russian);
        await russianTab.GotoAsync(site.BaseAddress.ToString());
        await russianTab.WaitForURLAsync(site.Url("ru/").ToString());

        site.AssertClean(storedTab);
        site.AssertClean(browserTab);
        site.AssertClean(russianTab);
    }

    [Fact]
    public async Task EveryBrowserRequestStaysUnderThePrefixOrIsGoatCounter()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await site.NewPageAsync(context);

        await tab.GotoAsync(site.Url("en/").ToString());
        await tab.Locator("a.btn--primary[href$='/en/dotnet/questions/']").First.ClickAsync();
        await tab.WaitForURLAsync(site.Url("en/dotnet/questions/").ToString());
        await tab.Locator("a.row__title").First.ClickAsync();
        await tab.WaitForURLAsync(url => url.Contains("/en/dotnet/questions/", StringComparison.Ordinal) && !url.EndsWith("/questions/", StringComparison.Ordinal));
        await tab.GotoAsync(site.Url("en/dotnet/topics/async/").ToString());
        await tab.Locator("a.row__title").First.ClickAsync();
        await tab.WaitForURLAsync(url => url.Contains("/en/dotnet/questions/", StringComparison.Ordinal));
        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        await tab.Locator("a[href$='/en/dotnet/profile/']").ClickAsync();
        await tab.WaitForURLAsync(site.Url("en/dotnet/profile/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        Assert.True(site.RequestUrls(tab).Count > 20, "the walk made too few requests to mean anything");
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Practice_RunsFromBrowserStorage_FetchingOnlyShownQuestionsAndTheNext()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, "en/dotnet/practice/");

        await tab.Locator(".pf__start").ClickAsync();
        await StaticSiteSteps.AnswerShownAsync(tab);
        await tab.Locator(".practice__next").ClickAsync();
        await StaticSiteSteps.AnswerShownAsync(tab);

        string[] data = StaticSiteSteps.DataRequests(site, tab);
        string[] shards = [.. data.Where(path => path.Contains("/data/en/q/", StringComparison.Ordinal))];
        Assert.Single(data, path => path.Contains("/data/en/dotnet/index.", StringComparison.Ordinal));
        // Two questions were shown and the third is the prefetched next one; the other six stay on the server.
        Assert.Equal(3, shards.Length);
        Assert.Equal(3, shards.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, data.Length);
        Assert.Equal(2, (await StaticSiteSteps.OutboxQuestionIdsAsync(tab)).Length);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Practice_NextQuestionOffline_ShowsRetryAndKeepsResults()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, "en/dotnet/practice/");
        int shardRequests = 0;
        bool online = false;
        await tab.RouteAsync("**/data/en/q/*.json", async route =>
        {
            // The first question loads; the next one (prefetched, then asked for again) meets a dead network.
            if (online || Interlocked.Increment(ref shardRequests) == 1)
            {
                await route.ContinueAsync();
            }
            else
            {
                await route.AbortAsync("internetdisconnected");
            }
        });

        await tab.Locator(".pf__start").ClickAsync();
        await StaticSiteSteps.AnswerShownAsync(tab);
        string[] answered = await StaticSiteSteps.OutboxQuestionIdsAsync(tab);
        await tab.Locator(".practice__next").ClickAsync();

        ILocator state = tab.Locator(".island-state");
        await state.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal(1, await state.Locator("button").CountAsync());
        Assert.Equal(StaticSiteSteps.Resource("State.TryAgain"), (await state.Locator("button").InnerTextAsync()).Trim());
        Assert.Equal(0, await tab.Locator(".qcard").CountAsync());
        Assert.Equal(answered, await StaticSiteSteps.OutboxQuestionIdsAsync(tab));
        Assert.Equal(1, await StaticSiteSteps.SavedAsync(tab));

        online = true;
        await state.Locator("button").ClickAsync();
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.StartsWith("Question 2 of", (await tab.Locator(".session-bar .visually-hidden").InnerTextAsync()).Trim(), StringComparison.Ordinal);
        Assert.Equal(answered, await StaticSiteSteps.OutboxQuestionIdsAsync(tab));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Practice_FirstLinkFromAQuestionPage_StartsWithThatQuestion()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url("en/dotnet/questions/result-deadlock-ui-thread/").ToString());
        string title = (await tab.Locator("h1").First.InnerTextAsync()).Trim();
        ILocator link = tab.Locator($"a[href*='first={StaticSiteSteps.SingleId}']");
        Assert.Equal(1, await link.CountAsync());

        await link.ClickAsync();
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        Assert.Equal(title, (await tab.Locator(".qcard__title").InnerTextAsync()).Trim());
        Assert.Contains("first=" + StaticSiteSteps.SingleId, tab.Url, StringComparison.Ordinal);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Review_ShowsSeededBackdatedAnswersAsDue()
    {
        await using IBrowserContext context = await NewContextAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await StaticSiteSteps.SeedOutboxAsync(
            context,
            StaticSiteSteps.OutboxJson(
            [
                ("01KC0000000000000000000001", StaticSiteSteps.SingleId, "didnt-know", now - TimeSpan.FromDays(3)),
                ("01KC0000000000000000000002", StaticSiteSteps.ClosureId, "knew", now - TimeSpan.FromHours(1)),
            ]));
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url("en/dotnet/review/").ToString());

        await tab.Locator(".qcard, .review-empty").First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        // Only the question answered wrongly three days ago is due: the one known an hour ago is not.
        Assert.Equal(0, await tab.Locator(".review-empty").CountAsync());
        Assert.Equal(
            "What happens when you call .Result in a WinForms button handler?",
            (await tab.Locator(".qcard__title").InnerTextAsync()).Trim());
        // Exactly one due question: a plain "contains 1" would also accept 10 or 21.
        Assert.Matches(@"^\D*\b1\b\D*$", await tab.Locator(".side__item[href$='review/']").InnerTextAsync());
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Progress_ACheckpointIsUsedOnlyWhileItMatchesTheLog()
    {
        await using IBrowserContext context = await NewContextAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await StaticSiteSteps.SeedOutboxAsync(
            context,
            StaticSiteSteps.OutboxJson(
            [
                ("01KC0000000000000000000001", StaticSiteSteps.SingleId, "didnt-know", now - TimeSpan.FromDays(3)),
                ("01KC0000000000000000000002", StaticSiteSteps.ClosureId, "knew", now - TimeSpan.FromHours(1)),
            ]));
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url("en/dotnet/review/").ToString());
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal("Question 1 of 1", (await tab.Locator(".session-bar .visually-hidden").InnerTextAsync()).Trim());

        // The first load replayed the log and stored the progress, tied to the log's fingerprint (computed by outbox.js).
        const string MatchesTheLog =
            """
            async () => {
              const fingerprint = await window.ditStore.fingerprint('dit.outbox.v1.anon');
              const raw = localStorage.getItem('dit.progress.v1.anon');
              return raw !== null && JSON.parse(raw).fingerprint === fingerprint && fingerprint.startsWith('2:');
            }
            """;
        await StaticSiteSteps.WaitUntilTrueAsync(tab, MatchesTheLog);

        // While it matches the log it is trusted, so a checkpoint that says both questions are due makes both due: only a
        // page that read it can show that, because the log alone says one.
        long due = (now - TimeSpan.FromDays(1)).UtcTicks;
        await tab.EvaluateAsync(
            "ticks => { const doc = JSON.parse(localStorage.getItem('dit.progress.v1.anon')); doc.states.forEach(s => { s.due = ticks; }); localStorage.setItem('dit.progress.v1.anon', JSON.stringify(doc)); }",
            due);
        await tab.GotoAsync(site.Url("en/dotnet/review/").ToString());
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal("Question 1 of 2", (await tab.Locator(".session-bar .visually-hidden").InnerTextAsync()).Trim());

        // The log moves on (an answer from another tab, which does not touch the checkpoint): the checkpoint no longer matches
        // it and is not believed. The page replays the log, and the checkpoint is rewritten for the log as it is now.
        await tab.EvaluateAsync(
            "async (answer) => { await window.ditStore.mutate(JSON.stringify({ op: 'append', key: 'dit.outbox.v1.anon', value: [answer] })); }",
            new
            {
                clientAnswerId = "01KC0000000000000000000003",
                questionId = StaticSiteSteps.ClosureId,
                grade = "knew",
                usedHint = false,
                mode = "practice",
                mockSessionId = (string?)null,
                answeredAt = now.ToString("O", CultureInfo.InvariantCulture),
            });
        await tab.GotoAsync(site.Url("en/dotnet/review/").ToString());
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal("Question 1 of 1", (await tab.Locator(".session-bar .visually-hidden").InnerTextAsync()).Trim());
        await StaticSiteSteps.WaitUntilTrueAsync(tab, MatchesTheLog.Replace("'2:'", "'3:'", StringComparison.Ordinal));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Mock_CompletesAndAppearsInTheProfile()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, "en/dotnet/mock/");
        const string progressKey = "dit.mock.v1.anon.active";

        await tab.Locator(".mock-setup input[name='mock-level'][value='middle']").CheckAsync();
        ILocator count = tab.Locator(".mock-setup input[name='count']");
        await count.FillAsync("3");
        await count.DispatchEventAsync("change");
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared", "answering");
        if (await tab.Locator(".mock[data-mock-phase='prepared']").CountAsync() > 0)
        {
            await tab.Locator(".mock-warn__anyway").ClickAsync();
        }

        await WaitForPhaseAsync(tab, "answering");
        int questions = await tab.Locator(".jump__btn").CountAsync();
        for (int i = 0; i < questions; i++)
        {
            await tab.Locator(".jump__btn").Nth(i).ClickAsync();
            await Assertions.Expect(tab.Locator(".jump__btn").Nth(i)).ToHaveAttributeAsync("aria-current", "step");
            await GiveResponseAsync(tab, progressKey);
        }

        await tab.Locator(".mock__finish").ClickAsync();
        await WaitForPhaseAsync(tab, "assessment", "summary");
        while (await tab.Locator(".mock[data-mock-phase='assessment']").CountAsync() > 0)
        {
            await tab.Locator(".mock-assess__item:has(.self__grades)").First.Locator(".self__grade--knew").ClickAsync();
            await WaitForPhaseAsync(tab, "assessment", "summary");
            await tab.WaitForFunctionAsync("() => !document.querySelector('.mock-assess [role=status]')", null, new PageWaitForFunctionOptions { Timeout = Wait });
        }

        await WaitForPhaseAsync(tab, "summary");
        await tab.GotoAsync(site.Url("en/dotnet/profile/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        ILocator mocks = tab.Locator(".profile__mocks li");
        Assert.Equal(1, await mocks.CountAsync());
        string text = await mocks.InnerTextAsync();
        Assert.Contains("middle", text, StringComparison.Ordinal);
        Assert.Contains(questions.ToString(CultureInfo.InvariantCulture) + " questions", text, StringComparison.Ordinal);
        // Choice questions are graded by the island, open ones by the assessment above; either way each one is counted once.
        System.Text.RegularExpressions.Match counts = System.Text.RegularExpressions.Regex.Match(
            await mocks.Locator(".profile__counts").InnerTextAsync(),
            @"knew (\d+) · partially (\d+) · didn't know (\d+) · unanswered (\d+)");
        Assert.True(counts.Success, "the counts line has an unexpected shape");
        Assert.Equal(questions, counts.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).Sum(g => int.Parse(g.Value, CultureInfo.InvariantCulture)));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task TwoTabs_KeepBothAnswers_AndATabThatNeverReloadedPlansFromBoth()
    {
        // What this proves: both answers are in the log, and a tab that was open the whole time, never reloaded, builds its next
        // session from both (its memoised progress was dropped by the storage event). What it does not prove is that the
        // sidebar of that tab changes by itself: the island reads its progress when it starts and when a session ends, and
        // nothing re-renders it on another tab's write, so that is not asserted.
        await using IBrowserContext context = await NewContextAsync();
        // The first tab only watches: it shows the filters, so what it picks later comes from the log as it is by then.
        IPage first = await StaticSiteSteps.OpenIslandAsync(site, context, "en/dotnet/practice/");
        IPage second = await StaticSiteSteps.OpenIslandAsync(site, context, $"en/dotnet/practice/?first={StaticSiteSteps.SelfAssessedAsyncId}");
        IPage third = await StaticSiteSteps.OpenIslandAsync(site, context, $"en/dotnet/practice/?first={StaticSiteSteps.EnglishOnlyAsyncId}");
        await second.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await third.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        await StaticSiteSteps.AnswerShownAsync(second);
        await StaticSiteSteps.AnswerShownAsync(third);

        Assert.Equal(
            new[] { StaticSiteSteps.SelfAssessedAsyncId, StaticSiteSteps.EnglishOnlyAsyncId }.Order(StringComparer.Ordinal),
            (await StaticSiteSteps.OutboxQuestionIdsAsync(first)).Order(StringComparer.Ordinal));
        // The first tab, never reloaded, plans its session from both answers: the async topic has five questions, two answered.
        await first.Locator(".pf input[name='topic'][value='async']").CheckAsync();
        await first.Locator(".pf input[name='status'][value='new']").CheckAsync();
        await first.Locator(".pf__start").ClickAsync();
        await first.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal("Question 1 of 3", (await first.Locator(".session-bar .visually-hidden").InnerTextAsync()).Trim());
        site.AssertClean(first);
        site.AssertClean(second);
        site.AssertClean(third);
    }

    [Fact]
    public async Task ReportProblem_LinksToThePrefilledIssue()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, $"en/dotnet/practice/?first={StaticSiteSteps.SingleId}");
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal(0, await tab.Locator(".report-link").CountAsync());

        await StaticSiteSteps.AnswerShownAsync(tab);

        string href = (await tab.Locator(".report-link a").GetAttributeAsync("href"))!;
        Assert.StartsWith("https://github.com/Platonenkov/dev-interview-trainer/issues/new?", href, StringComparison.Ordinal);
        Assert.Contains(StaticSiteSteps.SingleId, Uri.UnescapeDataString(href), StringComparison.Ordinal);
        Assert.Equal("_blank", await tab.Locator(".report-link a").GetAttributeAsync("target"));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task NotFound_ServesThe404PageWithWorkingLinks()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await site.NewPageAsync(context);

        IResponse? response = await tab.GotoAsync(site.Url("en/no-such-page/").ToString());

        Assert.Equal(404, response!.Status);
        await Assertions.Expect(tab.Locator("h1#not-found")).ToBeVisibleAsync();
        await tab.Locator("main a").First.ClickAsync();
        await tab.WaitForURLAsync(url => url.EndsWith("/en/", StringComparison.Ordinal) || url.EndsWith("/ru/", StringComparison.Ordinal));
        Assert.Equal(0, await tab.Locator("h1#not-found").CountAsync());
        Assert.Equal(1, await tab.Locator("h1").CountAsync());
        site.AssertClean(tab);
    }

    [Fact]
    public async Task NoPageSetsACookie()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        List<string> cookieHeaders = [];
        tab.Response += (_, response) =>
        {
            if (response.Headers.ContainsKey("set-cookie"))
            {
                lock (cookieHeaders)
                {
                    cookieHeaders.Add(response.Url);
                }
            }
        };
        string[] pages =
        [
            "", "en/", "en/about/", "en/dotnet/", "en/dotnet/questions/", "en/dotnet/topics/async/",
            "en/dotnet/questions/closure-for-loop/", "ru/dotnet/questions/", "en/missing/",
        ];
        foreach (string page in pages)
        {
            await tab.GotoAsync(site.Url(page).ToString());
            Assert.Equal(string.Empty, await tab.EvaluateAsync<string>("() => document.cookie"));
        }

        foreach (string island in new[] { "practice", "review", "mock", "profile" })
        {
            await tab.GotoAsync(site.Url($"en/dotnet/{island}/").ToString());
            await StaticSiteSteps.WaitForIslandAsync(tab);
            Assert.Equal(string.Empty, await tab.EvaluateAsync<string>("() => document.cookie"));
        }

        await tab.Locator(".theme-toggle").ClickAsync();
        Assert.Empty(await context.CookiesAsync());
        Assert.True(cookieHeaders.Count == 0, "Set-Cookie on: " + string.Join(", ", cookieHeaders));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task GoatCounterBeacon_CarriesNoQueryNoSameOriginReferrerAndNoHash()
    {
        await using IBrowserContext context = await NewContextAsync();
        // goatcounter-settings.js replaces window.goatcounter, so the opt-in for local hosts has to be set by a setter that
        // runs when it does; the shipped script ignores 127.0.0.1 otherwise.
        await context.AddInitScriptAsync(
            """
            (function () {
              var current;
              Object.defineProperty(window, 'goatcounter', {
                configurable: true,
                get: function () { return current; },
                set: function (value) { current = value; if (value && typeof value === 'object') { value.allow_local = true; } }
              });
            })();
            """);
        List<string> beacons = [];
        await context.RouteAsync($"https://{StaticSiteFixture.GoatCounterCode}.goatcounter.com/**", async route =>
        {
            lock (beacons)
            {
                beacons.Add(route.Request.Url);
            }

            await route.FulfillAsync(new RouteFulfillOptions { Status = 204 });
        });
        IPage tab = await site.NewPageAsync(context);

        await tab.GotoAsync(site.Url("en/dotnet/questions/").ToString());
        // The search form is a GET to the same page, so the second view has the first one (same origin) as its referrer
        // and the term in its query string.
        await tab.Locator("#catalog-search").FillAsync("secret-term");
        await tab.Locator(".catalog-search button[type=submit]").ClickAsync();
        await tab.WaitForURLAsync(url => url.Contains("q=secret-term", StringComparison.Ordinal));
        Assert.Equal(site.Url("en/dotnet/questions/").ToString(), await tab.EvaluateAsync<string>("() => document.referrer"));
        await tab.GotoAsync(site.Url("en/about/?q=other-term#frag").ToString());
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            lock (beacons)
            {
                if (beacons.Count >= 3)
                {
                    break;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "the page-view beacons did not arrive");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        string[] seen;
        lock (beacons)
        {
            seen = [.. beacons];
        }

        foreach (string beacon in seen)
        {
            Uri uri = new(beacon);
            Assert.Equal("/count", uri.AbsolutePath);
            System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.DoesNotContain("q", query.AllKeys);
            Assert.DoesNotContain("secret-term", beacon, StringComparison.Ordinal);
            Assert.DoesNotContain("other-term", beacon, StringComparison.Ordinal);
            Assert.DoesNotContain("frag", beacon, StringComparison.Ordinal);
            string path = query["p"]!;
            Assert.StartsWith(StaticSiteFixture.Prefix, path, StringComparison.Ordinal);
            Assert.DoesNotContain('?', path);
            Assert.DoesNotContain('#', path);
            // Same-origin referrers are dropped entirely.
            Assert.Null(query["r"]);
        }

        string[] paths = [.. seen.Select(beacon => System.Web.HttpUtility.ParseQueryString(new Uri(beacon).Query)["p"]!)];
        Assert.Equal(2, paths.Count(path => path == StaticSiteFixture.Prefix + "en/dotnet/questions/"));
        Assert.Contains(StaticSiteFixture.Prefix + "en/about/", paths);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task EndOfTestChecks_FailWhenTheirCondition_IsMet()
    {
        // The checks every test of the site ends with must be able to fail: a request that leaves the prefix, an inline script
        // the page policy forbids, and a cookie are each provoked once on purpose.
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url("en/about/").ToString());
        site.AssertClean(tab);

        await tab.EvaluateAsync("async url => { await fetch(url); }", new Uri(site.BaseAddress, "/stray-file.txt").ToString());
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => site.AssertRequestsStayUnderPrefix(tab));

        await tab.EvaluateAsync("() => { const script = document.createElement('script'); script.textContent = 'window.__inline = 1'; document.head.appendChild(script); }");
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                site.AssertNoCspViolations(tab);
            }
            catch (Xunit.Sdk.XunitException)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "an inline script did not raise a CSP violation");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await tab.EvaluateAsync("() => { document.cookie = 'probe=1; path=/'; }");
        Assert.NotEmpty(await context.CookiesAsync());
    }

    private static Task WaitForPhaseAsync(IPage tab, params string[] phases) =>
        tab.Locator(string.Join(", ", phases.Select(p => $".mock[data-mock-phase='{p}']")))
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

    private static async Task GiveResponseAsync(IPage tab, string progressKey)
    {
        const string RevisionScript = "k => { const v = localStorage.getItem(k); return v ? JSON.parse(v).revision : 0; }";
        long before = await tab.EvaluateAsync<long>(RevisionScript, progressKey);
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync();
        if (await tab.Locator(".opt--pick").CountAsync() > 0)
        {
            await tab.Locator(".opt--pick").First.ClickAsync();
        }
        else if (await tab.Locator(".code-line").CountAsync() > 0)
        {
            await tab.Locator(".code-line").First.ClickAsync();
        }
        else if (await tab.Locator(".typed__input").CountAsync() > 0)
        {
            await tab.Locator(".typed__input").FillAsync("x");
        }
        else
        {
            await tab.Locator(".self__notes").FillAsync("notes");
        }

        await tab.WaitForFunctionAsync(
            "([k, r]) => { const v = localStorage.getItem(k); return v !== null && JSON.parse(v).revision > r; }",
            new object[] { progressKey, before },
            new PageWaitForFunctionOptions { Timeout = Wait });
    }
}
