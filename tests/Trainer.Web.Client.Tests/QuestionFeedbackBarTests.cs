using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Localization;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class QuestionFeedbackBarTests
{
    private const long Owner = 4242;

    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (QuestionFeedbackModel Model, FakeHttpHandler Handler) Create(string? initialRating)
    {
        FakeTimeProvider device = new(DeviceStart);
        FakeHttpHandler handler = new();
        HttpClient http = handler.Client();
        TrainerApiClient api = new(http, new AntiforgeryTokenProvider(http), new ServerClock(device, new FakeBrowserStore()), device);
        return (new QuestionFeedbackModel(api, Owner, "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "en", initialRating), handler);
    }

    [Fact]
    public async Task Bar_PressingActiveRating_SendsClear()
    {
        (QuestionFeedbackModel model, FakeHttpHandler handler) = Create("like");
        handler.Respond = r => r.Method == HttpMethod.Delete ? FakeHttpHandler.Json(HttpStatusCode.OK, "{\"value\":null}") : null;

        await model.RateAsync("like", Ct);

        RecordedRequest request = Assert.Single(handler.Writes);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/questions/01KBZ3Q8N9V2K3M4P5R6S7T8WA/rating", request.Path);
        Assert.Equal("4242", request.Header("X-Outbox-Owner"));
        Assert.Null(model.Rating);
        Assert.Equal(FeedbackStatus.RatingSaved, model.Status);
    }

    [Fact]
    public async Task Bar_PressingOtherRating_PutsIt()
    {
        (QuestionFeedbackModel model, FakeHttpHandler handler) = Create("like");
        handler.Respond = r => r.Method == HttpMethod.Put ? FakeHttpHandler.Json(HttpStatusCode.OK, "{\"value\":\"dislike\"}") : null;

        await model.RateAsync("dislike", Ct);

        RecordedRequest request = Assert.Single(handler.Writes);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("{\"value\":\"dislike\"}", request.Body);
        Assert.Equal("dislike", model.Rating);
    }

    [Fact]
    public async Task Bar_FailedRating_KeepsTheStoredRating()
    {
        (QuestionFeedbackModel model, FakeHttpHandler handler) = Create("like");
        handler.Respond = r => r.Method == HttpMethod.Delete ? FakeHttpHandler.Error(HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError) : null;

        await model.RateAsync("like", Ct);

        Assert.Equal("like", model.Rating);
        Assert.Equal(FeedbackStatus.Failed, model.Status);
    }

    [Fact]
    public async Task Bar_Limit_ShowsLaterText_NotError()
    {
        (QuestionFeedbackModel model, FakeHttpHandler handler) = Create(null);
        handler.Respond = r => r.Path.EndsWith("/reports", StringComparison.Ordinal)
            ? FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.ReportLimit)
            : null;

        bool stored = await model.ReportAsync("outdated", "  ", Ct);

        Assert.False(stored);
        Assert.Equal(FeedbackStatus.Limit, model.Status);
        // A blank comment is not sent at all.
        Assert.Equal("{\"culture\":\"en\",\"reason\":\"outdated\",\"comment\":null}", Assert.Single(handler.Writes).Body);
        Assert.Equal("You can send more reports later", Text("QuestionFeedback.Limit", "en"));
        Assert.NotEqual(Text("QuestionFeedback.Failed", "en"), Text("QuestionFeedback.Limit", "en"));
        Assert.DoesNotContain('—', Text("QuestionFeedback.Limit", "ru"));
    }

    [Theory]
    [InlineData("{\"outcome\":\"created\"}", HttpStatusCode.Created, FeedbackStatus.ReportCreated)]
    [InlineData("{\"outcome\":\"updated\"}", HttpStatusCode.OK, FeedbackStatus.ReportUpdated)]
    public async Task Bar_ReportStored_ReportsItsOutcome(string body, HttpStatusCode status, FeedbackStatus expected)
    {
        (QuestionFeedbackModel model, FakeHttpHandler handler) = Create(null);
        handler.Respond = r => r.Path.EndsWith("/reports", StringComparison.Ordinal) ? FakeHttpHandler.Json(status, body) : null;

        bool stored = await model.ReportAsync("wrong-answer", "typo in the answer", Ct);

        Assert.True(stored);
        Assert.Equal(expected, model.Status);
    }

    [Fact]
    public async Task Bar_RendersToggles_FiveReasons_AndABoundedComment()
    {
        string html = await RenderAsync("like", "en");

        Assert.Contains("aria-pressed=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"false\"", html, StringComparison.Ordinal);
        Assert.Equal(5, Count(html, "type=\"radio\""));
        Assert.Contains("maxlength=\"1000\"", html, StringComparison.Ordinal);
        Assert.Contains("Report a problem", html, StringComparison.Ordinal);
        Assert.Contains("<details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        // The live region exists, empty, before any result.
        Assert.Matches("role=\"status\"[^>]*></p>", html);

        string russian = await RenderAsync(null, "ru");
        Assert.Contains("Сообщить о проблеме", russian, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-pressed=\"true\"", russian, StringComparison.Ordinal);
    }

    private static string Text(string key, string culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using ServiceProvider services = new ServiceCollection().AddLocalization().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider();
            return services.GetRequiredService<IStringLocalizer<ClientResource>>()[key].Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static async Task<string> RenderAsync(string? rating, string culture)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            FakeTimeProvider device = new(DeviceStart);
            HttpClient http = new FakeHttpHandler().Client();
            ServiceCollection services = new();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddLocalization();
            services.AddSingleton<TimeProvider>(device);
            services.AddSingleton(http);
            services.AddScoped<IBrowserStore, FakeBrowserStore>();
            services.AddScoped<ServerClock>();
            services.AddScoped<AntiforgeryTokenProvider>();
            services.AddScoped<TrainerApiClient>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
            Dictionary<string, object?> parameters = new()
            {
                [nameof(QuestionFeedbackBar.Question)] = TestQuestions.Single(),
                [nameof(QuestionFeedbackBar.InitialRating)] = rating,
                [nameof(QuestionFeedbackBar.Owner)] = Owner,
                [nameof(QuestionFeedbackBar.Culture)] = culture,
            };
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                HtmlRootComponent root = await renderer.RenderComponentAsync<QuestionFeedbackBar>(ParameterView.FromDictionary(parameters));
                return WebUtility.HtmlDecode(root.ToHtmlString()).Trim();
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static int Count(string text, string needle) =>
        text.Split(needle, StringSplitOptions.None).Length - 1;
}
