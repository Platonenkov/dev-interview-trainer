using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Interop;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// Renders the island components to HTML with the framework's own <see cref="HtmlRenderer"/>, so markup rules (no inline
/// styles, ARIA state, sizes through progress and meter, the right strings) are checked without a browser. Behaviour in
/// a real browser is covered by the mode E2E tests.
/// </summary>
public sealed class IslandComponentRenderTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OptionList_RendersNativeInputsWithHints_AndNoInlineStyles()
    {
        QuestionAttempt single = Attempt(TestQuestions.Single(correct: "b"));
        single.Toggle("c");

        string html = await RenderAsync<OptionList>(new() { [nameof(OptionList.Attempt)] = single });

        Assert.Equal(4, Count(html, "type=\"radio\""));
        Assert.Equal(1, Count(html, "checked"));
        Assert.Contains("<kbd class=\"kbd-hint\" aria-hidden=\"true\">4</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("Answer options: pick one", html, StringComparison.Ordinal);
        Assert.DoesNotContain("opt--good", html, StringComparison.Ordinal);
        AssertNoInlineStyle(html);

        single.SubmitAutomatic();
        string answered = await RenderAsync<OptionList>(new()
        {
            [nameof(OptionList.Attempt)] = single,
            [nameof(OptionList.ShowVerdicts)] = true,
        });
        Assert.Contains("disabled", answered, StringComparison.Ordinal);
        Assert.Equal(1, Count(answered, "opt--good"));
        Assert.Equal(1, Count(answered, "opt--bad"));

        QuestionAttempt multi = Attempt(TestQuestions.Multi());
        string checkboxes = await RenderAsync<OptionList>(new() { [nameof(OptionList.Attempt)] = multi });
        Assert.Equal(4, Count(checkboxes, "type=\"checkbox\""));
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", true)]
    public async Task LocalTime_FormatsThroughTheBrowserInTheUiLanguage(string culture, bool withTime)
    {
        DateTimeOffset value = new(2026, 3, 7, 21, 30, 0, TimeSpan.Zero);

        string html = await RenderAsync<LocalTime>(
            new() { [nameof(LocalTime.Value)] = value, [nameof(LocalTime.WithTime)] = withTime },
            culture);

        string expected = $"<time datetime=\"2026-03-07T21:30:00.0000000+00:00\">{culture}|1772919000000|{withTime}</time>";
        Assert.Equal(expected, html.Trim());
    }

    [Fact]
    public async Task CodeLineSelector_RendersPressedButtonsPerLine()
    {
        QuestionAttempt attempt = Attempt(TestQuestions.FindBug());
        attempt.ToggleLine(2);

        string html = await RenderAsync<CodeLineSelector>(new() { [nameof(CodeLineSelector.Attempt)] = attempt });

        Assert.Equal(4, Count(html, "<button type=\"button\" class=\"code-line\""));
        Assert.Equal(1, Count(html, "aria-pressed=\"true\""));
        Assert.Equal(3, Count(html, "aria-pressed=\"false\""));
        Assert.Contains("<span class=\"visually-hidden\">Line 2 </span><span class=\"line\" data-line=\"2\">b</span>", html, StringComparison.Ordinal);
        AssertNoInlineStyle(html);
    }

    [Fact]
    public async Task QuestionCard_ShowsCodePanelExceptForFindBug_AndHintButtonUntilUsed()
    {
        QuestionAttempt output = Attempt(TestQuestions.OutputTyped());

        string html = await RenderAsync<QuestionCard>(new() { [nameof(QuestionCard.Attempt)] = output });

        Assert.Contains("<pre class=\"code\" role=\"group\" tabindex=\"0\" aria-label=\"Code\">", html, StringComparison.Ordinal);
        Assert.Contains(">Show hint</button>", html, StringComparison.Ordinal);

        output.RevealHint();
        string hinted = await RenderAsync<QuestionCard>(new() { [nameof(QuestionCard.Attempt)] = output });
        Assert.DoesNotContain("Show hint", hinted, StringComparison.Ordinal);
        Assert.Contains("<p>hint</p>", hinted, StringComparison.Ordinal);

        string findBug = await RenderAsync<QuestionCard>(new() { [nameof(QuestionCard.Attempt)] = Attempt(TestQuestions.FindBug()) });
        Assert.DoesNotContain("<pre class=\"code\"", findBug, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedbackPanel_StatusHoldsOnlyTheVerdict_AndTerminalShowsExpectedAndRefs()
    {
        QuestionAttempt attempt = Attempt(TestQuestions.OutputTyped("333"));
        string before = await RenderAsync<FeedbackPanel>(new() { [nameof(FeedbackPanel.Attempt)] = attempt });
        Assert.Contains("<p class=\"visually-hidden\" role=\"status\"></p>", before, StringComparison.Ordinal);
        Assert.DoesNotContain("term", before, StringComparison.Ordinal);

        attempt.SetTyped("012");
        attempt.SubmitAutomatic();
        string html = await RenderAsync<FeedbackPanel>(new() { [nameof(FeedbackPanel.Attempt)] = attempt });

        Assert.Contains("<p class=\"visually-hidden\" role=\"status\">Incorrect</p>", html, StringComparison.Ordinal);
        Assert.Contains("expected <code class=\"feedback__value\">333</code>, got <code class=\"feedback__value\">012</code>", html, StringComparison.Ordinal);
        Assert.Contains("> explain</p>", html, StringComparison.Ordinal);
        Assert.Contains("> refs</p>", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "role=\"status\""));
        AssertNoInlineStyle(html);
    }

    [Fact]
    public async Task FeedbackPanel_FindBugShowsLinesAndFix_SelfAssessedShowsTheUsersGrade()
    {
        QuestionAttempt bug = Attempt(TestQuestions.FindBug());
        bug.ToggleLine(1);
        bug.SubmitAutomatic();
        string bugHtml = await RenderAsync<FeedbackPanel>(new() { [nameof(FeedbackPanel.Attempt)] = bug });
        Assert.Contains("the bug is in lines 2, you marked 1", bugHtml, StringComparison.Ordinal);
        Assert.Contains("> fix</p>", bugHtml, StringComparison.Ordinal);

        QuestionAttempt open = Attempt(TestQuestions.Open());
        open.RevealHint();
        open.RevealReference();
        await open.SubmitSelfAssessmentAsync(Grade.Knew, new SelfAssessmentGrader(), Ct);
        string openHtml = await RenderAsync<FeedbackPanel>(new() { [nameof(FeedbackPanel.Attempt)] = open });
        Assert.Contains("role=\"status\">Your grade: Knew it</p>", openHtml, StringComparison.Ordinal);
        Assert.Contains("Hint used, so the best grade is Partially", openHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfAssessment_GradesAppearOnlyAfterReveal()
    {
        QuestionAttempt open = Attempt(TestQuestions.Open());

        string before = await RenderAsync<SelfAssessment>(new() { [nameof(SelfAssessment.Attempt)] = open, [nameof(SelfAssessment.AllowSkip)] = true });
        open.RevealReference();
        string after = await RenderAsync<SelfAssessment>(new() { [nameof(SelfAssessment.Attempt)] = open, [nameof(SelfAssessment.AllowSkip)] = true });

        Assert.Contains("Show reference answer", before, StringComparison.Ordinal);
        Assert.DoesNotContain("Knew it", before, StringComparison.Ordinal);
        Assert.Contains("<p>answer</p>", after, StringComparison.Ordinal);
        Assert.Contains("<kbd class=\"kbd-hint\" aria-hidden=\"true\">3</kbd>Didn't know", after, StringComparison.Ordinal);
        Assert.Contains(">Skip</button>", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionBar_UsesProgressElementAndPluralStreak()
    {
        string html = await RenderAsync<SessionBar>(new()
        {
            [nameof(SessionBar.Level)] = "middle",
            [nameof(SessionBar.TopicTitle)] = "Closures",
            [nameof(SessionBar.Position)] = 7,
            [nameof(SessionBar.Count)] = 20,
            [nameof(SessionBar.Completed)] = 6,
            [nameof(SessionBar.StreakDays)] = 5,
        });

        Assert.Contains("<span class=\"level-chip level-middle\">middle</span>", html, StringComparison.Ordinal);
        Assert.Contains("<progress max=\"20\" value=\"6\" aria-label=\"Session progress\">", html, StringComparison.Ordinal);
        Assert.Contains("streak 5 days", html, StringComparison.Ordinal);
        Assert.Contains("Question 7 of 20", html, StringComparison.Ordinal);
        AssertNoInlineStyle(html);
    }

    [Fact]
    public async Task TopicSidebar_SignedInShowsMetersAndReviewQueue_AnonymousShowsNamesOnly()
    {
        TopicDto[] topics = [new("closures", "Closures"), new("gc", "CLR & GC")];
        MeDto me = new(42, 5, 14, [new TopicProgressDto("closures", "Closures", 10, 6, 4, 2)]);
        Dictionary<string, object?> parameters = new()
        {
            [nameof(TopicSidebar.Topics)] = topics,
            [nameof(TopicSidebar.Culture)] = "en",
            [nameof(TopicSidebar.Stack)] = "dotnet",
            [nameof(TopicSidebar.CurrentTopic)] = "closures",
        };

        string anonymous = await RenderAsync<TopicSidebar>(parameters);
        parameters[nameof(TopicSidebar.Me)] = me;
        string signedIn = await RenderAsync<TopicSidebar>(parameters);

        Assert.DoesNotContain("<meter", anonymous, StringComparison.Ordinal);
        Assert.DoesNotContain("review", anonymous, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/practice?topic=closures\" aria-current=\"true\" data-enhance-nav=\"false\"", anonymous, StringComparison.Ordinal);
        Assert.Contains("<meter class=\"side__meter\" min=\"0\" max=\"10\" value=\"4\" aria-hidden=\"true\">", signedIn, StringComparison.Ordinal);
        Assert.Contains("40%", signedIn, StringComparison.Ordinal);
        Assert.Contains("review queue · 14", signedIn, StringComparison.Ordinal);
        AssertNoInlineStyle(signedIn);
    }

    [Theory]
    [InlineData(IslandStateKind.Loading, "Loading questions")]
    [InlineData(IslandStateKind.Empty, "No questions match these filters")]
    [InlineData(IslandStateKind.Offline, "You are offline; starting a session needs the network")]
    [InlineData(IslandStateKind.RateLimited, "Too many requests; try again in 17 seconds")]
    [InlineData(IslandStateKind.SignedOut, "href=\"/en/signin?returnUrl=%2Fen%2Fdotnet%2Fpractice%3Ftopic%3Dclosures\"")]
    [InlineData(IslandStateKind.ServerError, "Something went wrong")]
    public async Task IslandStateView_RendersEveryState(IslandStateKind kind, string expected)
    {
        string html = await RenderAsync<IslandStateView>(new()
        {
            [nameof(IslandStateView.Kind)] = kind,
            [nameof(IslandStateView.RetryAfter)] = TimeSpan.FromSeconds(16.2),
        });

        Assert.Contains(expected, html, StringComparison.Ordinal);
        AssertNoInlineStyle(html);
    }

    [Theory]
    [InlineData(90, "Too many requests; try again in 90 seconds")]
    [InlineData(120, "Too many requests; try again in 2 minutes")]
    [InlineData(3600, "Too many requests; try again in 60 minutes")]
    [InlineData(61 * 60 + 1, "Too many requests; try again in 62 minutes")]
    public async Task IslandStateView_LongWaits_AreShownInMinutes(int seconds, string expected)
    {
        string html = await RenderAsync<IslandStateView>(new()
        {
            [nameof(IslandStateView.Kind)] = IslandStateKind.RateLimited,
            [nameof(IslandStateView.RetryAfter)] = TimeSpan.FromSeconds(seconds),
        });

        Assert.Contains(expected, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountChanged_FromAnIslandStateAndTheShell_IsOneNotice_InEitherOrder(bool shellFirst)
    {
        RenderFragment shell = b =>
        {
            b.OpenComponent<AccountChangedNotice>(0);
            b.CloseComponent();
        };
        RenderFragment state = b =>
        {
            b.OpenComponent<IslandStateView>(0);
            b.AddComponentParameter(1, nameof(IslandStateView.Kind), IslandStateKind.AccountChanged);
            b.CloseComponent();
        };

        string html = await RenderAsync<FragmentHost>(new()
        {
            [nameof(FragmentHost.Content)] = (RenderFragment)(b =>
            {
                b.AddContent(0, shellFirst ? shell : state);
                b.AddContent(1, shellFirst ? state : shell);
            }),
        });

        Assert.Equal(1, Count(html, "You signed in or out in another tab. Reload to continue."));
        Assert.Equal(1, Count(html, "role=\"alert\""));
    }

    [Fact]
    public async Task QuestionCard_HidesTheHintOnceTheReferenceIsShown()
    {
        QuestionAttempt open = Attempt(TestQuestions.Open());
        string before = await RenderAsync<QuestionCard>(new() { [nameof(QuestionCard.Attempt)] = open });
        open.RevealReference();

        string after = await RenderAsync<QuestionCard>(new() { [nameof(QuestionCard.Attempt)] = open });

        Assert.Contains("Show hint", before, StringComparison.Ordinal);
        Assert.DoesNotContain("Show hint", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfAssessment_GradeButtonsAreDisabledWhileGrading()
    {
        QuestionAttempt open = Attempt(TestQuestions.Open());
        open.RevealReference();
        TaskCompletionSource<Grade> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<Grade> grading = open.SubmitSelfAssessmentAsync(Grade.Partial, new SlowGrader(result.Task), Ct);

        string html = await RenderAsync<SelfAssessment>(new() { [nameof(SelfAssessment.Attempt)] = open, [nameof(SelfAssessment.AllowSkip)] = true });
        result.SetResult(Grade.Partial);
        await grading;

        Assert.Equal(4, Count(html, "disabled"));
    }

    [Fact]
    public async Task OptionList_ColumnClassFollowsTheOptions()
    {
        string shortOptions = await RenderAsync<OptionList>(new() { [nameof(OptionList.Attempt)] = Attempt(TestQuestions.Single()) });
        string longOptions = await RenderAsync<OptionList>(new()
        {
            [nameof(OptionList.Attempt)] = Attempt(TestQuestions.Single() with
            {
                Options = [new OptionDto("a", "<p>The UI hangs forever because of a deadlock</p>", true, null), new OptionDto("b", "<p>b</p>", false, null)],
            }),
        });

        Assert.Contains("class=\"opts opts--pick opts--fit-4\"", shortOptions, StringComparison.Ordinal);
        Assert.Contains("class=\"opts opts--pick opts--fit-2\"", longOptions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IslandStateView_Ready_RendersNothing() =>
        Assert.Equal("", await RenderAsync<IslandStateView>(new() { [nameof(IslandStateView.Kind)] = IslandStateKind.Ready }));

    [Fact]
    public async Task IslandStateView_RuCulture_UsesRuStrings()
    {
        string html = await RenderAsync<IslandStateView>(
            new() { [nameof(IslandStateView.Kind)] = IslandStateKind.RateLimited, [nameof(IslandStateView.RetryAfter)] = TimeSpan.FromSeconds(3) },
            "ru");

        Assert.Contains("Слишком много запросов; повторите через 3 секунды", html, StringComparison.Ordinal);
    }

    private static QuestionAttempt Attempt(QuestionDto question) =>
        new(question, TestQuestions.NewId(), new Random(1), new ServerClock(new FakeTimeProvider(DeviceStart), new FakeBrowserStore()));

    private static int Count(string html, string value)
    {
        int count = 0;
        for (int at = html.IndexOf(value, StringComparison.Ordinal); at >= 0; at = html.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void AssertNoInlineStyle(string html) =>
        Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters, string culture = "en")
        where TComponent : IComponent
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo previousFormat = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            await using ServiceProvider services = Services();
            await using HtmlRenderer renderer = new(services, NullLoggerFactory.Instance);
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
                // Decoded, so assertions read text as people do; the markup itself stays as rendered.
                return WebUtility.HtmlDecode(root.ToHtmlString()).Trim();
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    // The same registrations as Program.cs, with fakes for the browser.
    private static ServiceProvider Services()
    {
        ServiceCollection services = new();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLocalization();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DeviceStart));
        services.AddSingleton<IJSRuntime, FakeJsRuntime>();
        services.AddSingleton<ILocalTimeFormatter, RecordingTimeFormatter>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new FakeHttpHandler().Client());
        services.AddScoped<IBrowserStore, FakeBrowserStore>();
        services.AddScoped<OutboxStore>();
        services.AddScoped<SummaryStore>();
        services.AddScoped<ActiveMockStore>();
        services.AddScoped<ServerClock>();
        services.AddScoped<AntiforgeryTokenProvider>();
        services.AddScoped<TrainerApiClient>();
        services.AddScoped<OutboxSyncService>();
        services.AddScoped<AccountWatcher>();
        services.AddScoped<AccountNotice>();
        return services.BuildServiceProvider();
    }

    private sealed class SlowGrader(Task<Grade> result) : IAnswerGrader
    {
        public async ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct) => await result;
    }

    /// <summary>Renders several components in one tree, so they share the services of one page.</summary>
    private sealed class FragmentHost : ComponentBase
    {
        [Parameter]
        public RenderFragment? Content { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, Content);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/en/dotnet/practice?topic=closures");
    }

    /// <summary>Echoes what the browser helper would be asked: language, epoch milliseconds and whether to show the time.</summary>
    private sealed class RecordingTimeFormatter : ILocalTimeFormatter
    {
        public string Format(string language, DateTimeOffset value, bool withTime) =>
            string.Create(CultureInfo.InvariantCulture, $"{language}|{value.ToUnixTimeMilliseconds()}|{withTime}");
    }
}
