using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.Application.Feedback;
using Trainer.Application.Users;
using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// Rate and report on the question page (ADR 0016): plain POST forms for a signed-in request only, so the cached anonymous
/// HTML stays free of forms, tokens and cookies (ADR 0009, ADR 0012). Uses the site that allows one new report a day.
/// </summary>
public sealed partial class QuestionFeedbackPageTests(FeedbackSiteFixture site, PostgresFixture postgres) : IClassFixture<FeedbackSiteFixture>
{
    private const string Path = "/en/dotnet/questions/closure-for-loop";
    private const string OtherPath = "/en/dotnet/questions/configureawait-false-purpose";
    private const string Anchor = "#feedback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<form\\b[^>]*method=\"post\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PostForm();

    [GeneratedRegex("<input\\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Input();

    [GeneratedRegex("(?<name>[a-z-]+)=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    private static bool IsHit(HttpResponseMessage response) => response.Headers.Contains("Age");

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The hidden fields of the post form named <paramref name="handler"/>, as a browser would submit them.</summary>
    private static Dictionary<string, string> HiddenFieldsOf(string html, string handler)
    {
        foreach (Match form in PostForm().Matches(html))
        {
            string body = form.Groups["body"].Value;
            if (!body.Contains($"value=\"{handler}\"", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            foreach (Match input in Input().Matches(body))
            {
                Dictionary<string, string> attributes = Attribute().Matches(input.Groups["attrs"].Value)
                    .ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
                if (attributes.GetValueOrDefault("type") == "hidden" && attributes.TryGetValue("name", out string? name))
                {
                    fields[name] = attributes.GetValueOrDefault("value", "");
                }
            }

            return fields;
        }

        throw new InvalidOperationException($"No post form is named '{handler}'.");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields), Ct);

    private async Task<HttpClient> SignedInAsync(long telegramId) => await SignInAsync(site.Factory, site.Telegram, telegramId, Ct);

    private static async Task<Dictionary<string, string>> RatingFormAsync(HttpClient client, string path, string value)
    {
        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage _ = response;
        Dictionary<string, string> fields = HiddenFieldsOf(body, "question-rating");
        fields["value"] = value;
        return fields;
    }

    private static async Task<Dictionary<string, string>> ReportFormAsync(HttpClient client, string path, string? reason, string? comment)
    {
        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage _ = response;
        Dictionary<string, string> fields = HiddenFieldsOf(body, "question-report");
        if (reason is not null)
        {
            fields["reason"] = reason;
        }

        fields["comment"] = comment ?? "";
        return fields;
    }

    private Task<int> RatingsAsync(long telegramId, string questionId = ClosureQuestionId) => CountAsync(
        site.Factory,
        $"""SELECT count(*)::int AS "Value" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId} AND r.question_id = {questionId}""",
        Ct);

    private Task<int> ReportsAsync(long telegramId) => CountAsync(
        site.Factory,
        $"""SELECT count(*)::int AS "Value" FROM question_reports r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}""",
        Ct);

    [Fact]
    public async Task QuestionPage_Anonymous_HasNoFeedbackFormNoTokenNoSetCookie_AndIsCached()
    {
        using HttpClient anonymous = Bare(site.Factory);

        (HttpResponseMessage first, string firstBody) = await GetAsync(anonymous, Path);
        (HttpResponseMessage second, string secondBody) = await GetAsync(anonymous, Path);
        // The feedback query is no part of the cache key and is never read for an anonymous request.
        (HttpResponseMessage withNotice, string withNoticeBody) = await GetAsync(anonymous, Path + "?feedback=rated");
        using (first)
        using (second)
        using (withNotice)
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.True(IsHit(second), "The second anonymous request should be a cache hit.");
            Assert.True(IsHit(withNotice), "The feedback query must not change the cache entry.");
            Assert.Equal(firstBody, secondBody);
            Assert.Equal(firstBody, withNoticeBody);
            foreach (HttpResponseMessage response in new[] { first, second, withNotice })
            {
                Assert.False(response.Headers.Contains("Set-Cookie"));
            }
        }

        Assert.DoesNotContain("<form", firstBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__RequestVerificationToken", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"feedback\"", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Thank you, your rating is saved.", withNoticeBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_SignedIn_RendersFormsWithAntiforgeryAndCurrentRating()
    {
        const long Owner = 700_101L;
        using HttpClient client = await SignedInAsync(Owner);
        UserAccess access = await AccessOfAsync(site.Factory, Owner, Ct);

        (HttpResponseMessage unrated, string unratedBody) = await GetAsync(client, Path);
        using (unrated)
        {
            Assert.Equal(HttpStatusCode.OK, unrated.StatusCode);
            Assert.Contains("<section id=\"feedback\"", unratedBody, StringComparison.Ordinal);
            Assert.Contains("Rate this question", unratedBody, StringComparison.Ordinal);
            Assert.Contains("__RequestVerificationToken", HiddenFieldsOf(unratedBody, "question-rating").Keys);
            Assert.Contains("__RequestVerificationToken", HiddenFieldsOf(unratedBody, "question-report").Keys);
            Assert.Equal(2, Regex.Count(unratedBody, "aria-pressed=\"false\"", RegexOptions.CultureInvariant));
            Assert.DoesNotContain("aria-pressed=\"true\"", unratedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("value=\"clear\"", unratedBody, StringComparison.Ordinal);
            Assert.Contains("<textarea", unratedBody, StringComparison.Ordinal);
            Assert.Contains("maxlength=\"1000\"", unratedBody, StringComparison.Ordinal);
            Assert.Equal(ReportReasonKeys.All.Count, Regex.Count(unratedBody, "type=\"radio\" name=\"reason\"", RegexOptions.CultureInvariant));
        }

        await using (AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IQuestionFeedbackService>()
                .SetRatingAsync(access.UserId, new QuestionId(ClosureQuestionId), QuestionRating.Dislike, Ct);
        }

        (HttpResponseMessage rated, string ratedBody) = await GetAsync(client, Path);
        using (rated)
        {
            Assert.Matches("<button[^>]*value=\"dislike\"[^>]*aria-pressed=\"true\"", ratedBody);
            Assert.Matches("<button[^>]*value=\"like\"[^>]*aria-pressed=\"false\"", ratedBody);
            Assert.Contains("value=\"clear\"", ratedBody, StringComparison.Ordinal);
            Assert.Contains("Remove my rating", ratedBody, StringComparison.Ordinal);
        }

        (HttpResponseMessage ru, string ruBody) = await GetAsync(client, "/ru/dotnet/questions/closure-for-loop");
        using (ru)
        {
            Assert.Contains("Оцените вопрос", ruBody, StringComparison.Ordinal);
            Assert.Contains("Сообщить о проблеме", ruBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Rate this question", ruBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QuestionPage_SignedIn_RetiredQuestion_HasNoFeedbackForms()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Telegram = telegram });
        await factory.WaitUntilReadyAsync();
        factory.Content.Delete("dotnet/async/to-be-retired.yaml");
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ContentImportResult result = await scope.ServiceProvider.GetRequiredService<IContentImportService>().ImportAsync(factory.Content.Root, Ct);
            Assert.Equal(ContentImportStatus.Imported, result.Status);
        }

        using HttpClient client = await SignInAsync(factory, telegram, 700_102L, Ct);
        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/questions/to-be-retired");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("has been retired", body, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"feedback\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("question-rating", body, StringComparison.Ordinal);
            Assert.DoesNotContain("question-report", body, StringComparison.Ordinal);
        }
    }

    private static async Task RetireTopicOfClosureQuestionAsync(TrainerWebFactory factory)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(factory.ConnectionString);
        int updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE topics SET retired_at = now()
            WHERE (stack_key, key) IN (SELECT stack_key, topic_key FROM questions WHERE id = {ClosureQuestionId})
            """,
            Ct);
        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task QuestionPage_SignedIn_QuestionInRetiredTopic_HasNoFeedbackForms()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Telegram = telegram });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = await SignInAsync(factory, telegram, 700_120L, Ct);
        (HttpResponseMessage live, string liveBody) = await GetAsync(client, Path);
        using (live)
        {
            Assert.Contains("id=\"feedback\"", liveBody, StringComparison.Ordinal);
        }

        await RetireTopicOfClosureQuestionAsync(factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, Path);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("id=\"feedback\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("question-rating", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("rating", "/en/dotnet/questions/closure-for-loop", "This question is no longer available, so it cannot be rated or reported.")]
    [InlineData("report", "/ru/dotnet/questions/closure-for-loop", "Этот вопрос больше недоступен, поэтому оценить его или отправить жалобу нельзя.")]
    public async Task Post_QuestionBecameUnavailable_RedirectsWithTheUnavailableNoticeAndStoresNothing(string kind, string path, string notice)
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Telegram = telegram });
        await factory.WaitUntilReadyAsync();
        long owner = kind == "rating" ? 700_121L : 700_122L;
        using HttpClient client = await SignInAsync(factory, telegram, owner, Ct);
        Dictionary<string, string> fields = kind == "rating"
            ? await RatingFormAsync(client, path, "like")
            : await ReportFormAsync(client, path, "other", "outdated");
        await RetireTopicOfClosureQuestionAsync(factory);

        using HttpResponseMessage response = await PostAsync(client, path, fields);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.EndsWith(path + "?feedback=unavailable" + Anchor, response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(
            factory,
            $"""SELECT count(*)::int AS "Value" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {owner}""",
            Ct));
        Assert.Equal(0, await CountAsync(
            factory,
            $"""SELECT count(*)::int AS "Value" FROM question_reports r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {owner}""",
            Ct));

        (HttpResponseMessage page, string body) = await GetAsync(client, path + "?feedback=unavailable");
        using (page)
        {
            Assert.Contains(notice, body, StringComparison.Ordinal);
            Assert.DoesNotContain("question-rating", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RatingPost_Redirects302ToFeedbackAnchor_AndStores()
    {
        const long Owner = 700_103L;
        using HttpClient client = await SignedInAsync(Owner);

        using HttpResponseMessage liked = await PostAsync(client, Path, await RatingFormAsync(client, Path, "like"));

        Assert.Equal(HttpStatusCode.Found, liked.StatusCode);
        Assert.EndsWith(Path + "?feedback=rated" + Anchor, liked.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await RatingsAsync(Owner));

        // Following the redirect shows the notice and the pressed state.
        (HttpResponseMessage page, string body) = await GetAsync(client, Path + "?feedback=rated");
        using (page)
        {
            Assert.Contains("Thank you, your rating is saved.", body, StringComparison.Ordinal);
            Assert.Matches("<button[^>]*value=\"like\"[^>]*aria-pressed=\"true\"", body);
        }

        using HttpResponseMessage cleared = await PostAsync(client, Path, await RatingFormAsync(client, Path, "clear"));

        Assert.Equal(HttpStatusCode.Found, cleared.StatusCode);
        Assert.EndsWith(Path + "?feedback=cleared" + Anchor, cleared.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await RatingsAsync(Owner));
    }

    [Fact]
    public async Task RatingPost_UnknownValue_RedirectsWithInvalidAndStoresNothing()
    {
        const long Owner = 700_104L;
        using HttpClient client = await SignedInAsync(Owner);

        using HttpResponseMessage response = await PostAsync(client, Path, await RatingFormAsync(client, Path, "love"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.EndsWith(Path + "?feedback=invalid" + Anchor, response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await RatingsAsync(Owner));
    }

    [Fact]
    public async Task RatingPost_WithoutAntiforgeryToken_Returns400()
    {
        const long Owner = 700_105L;
        using HttpClient client = await SignedInAsync(Owner);
        Dictionary<string, string> fields = await RatingFormAsync(client, Path, "like");
        fields.Remove("__RequestVerificationToken");

        using HttpResponseMessage response = await PostAsync(client, Path, fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RatingsAsync(Owner));
    }

    [Fact]
    public async Task RatingPost_Anonymous_DoesNotStore()
    {
        // A token an anonymous visitor can legitimately hold, posted to the form handler of a page that renders no form for them.
        using HttpClient client = Browser(site.Factory);
        string token = await GetTokenAsync(client, Ct);
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["_handler"] = "question-rating",
            ["value"] = "like",
            ["__RequestVerificationToken"] = token,
        };
        int before = await TotalRatingsAsync(ConfigureAwaitQuestionId);

        using HttpResponseMessage response = await PostAsync(client, OtherPath, fields);

        Assert.False(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(before, await TotalRatingsAsync(ConfigureAwaitQuestionId));
    }

    private Task<int> TotalRatingsAsync(string questionId) => CountAsync(
        site.Factory, $"""SELECT count(*)::int AS "Value" FROM question_ratings WHERE question_id = {questionId}""", Ct);

    [Fact]
    public async Task ReportPost_OverLimit_RedirectsWithReportLimitNotice()
    {
        const long Owner = 700_106L;
        using HttpClient client = await SignedInAsync(Owner);

        using HttpResponseMessage created = await PostAsync(client, Path, await ReportFormAsync(client, Path, "other", "  first line\r\nsecond line  "));
        Assert.Equal(HttpStatusCode.Found, created.StatusCode);
        Assert.EndsWith(Path + "?feedback=reported" + Anchor, created.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await ReportsAsync(Owner));

        // The allowance is one new report a day, so another reason is refused ...
        using HttpResponseMessage limited = await PostAsync(client, Path, await ReportFormAsync(client, Path, "outdated", "x"));
        Assert.Equal(HttpStatusCode.Found, limited.StatusCode);
        Assert.EndsWith(Path + "?feedback=report-limit" + Anchor, limited.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await ReportsAsync(Owner));

        // ... while editing the open report of the same reason is not counted.
        using HttpResponseMessage updated = await PostAsync(client, Path, await ReportFormAsync(client, Path, "other", "changed"));
        Assert.Equal(HttpStatusCode.Found, updated.StatusCode);
        Assert.EndsWith(Path + "?feedback=report-updated" + Anchor, updated.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await ReportsAsync(Owner));

        (HttpResponseMessage page, string body) = await GetAsync(client, Path + "?feedback=report-limit");
        using (page)
        {
            Assert.Contains("daily limit of reports", body, StringComparison.Ordinal);
            Assert.Contains("feedback-forms__notice--warn", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReportPost_StoresReasonCultureAndNormalisedComment()
    {
        const long Owner = 700_107L;
        using HttpClient client = await SignedInAsync(Owner);
        const string ruPath = "/ru/dotnet/questions/closure-for-loop";

        using HttpResponseMessage response = await PostAsync(client, ruPath, await ReportFormAsync(client, ruPath, "wrong-answer", "  a\r\nb  "));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.EndsWith(ruPath + "?feedback=reported" + Anchor, response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(
            site.Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM question_reports r JOIN users u ON u.id = r.user_id
            WHERE u.telegram_user_id = {Owner} AND r.reason = {(int)ReportReason.WrongAnswer} AND r.culture = 'ru' AND r.comment = E'a\nb'
            """,
            Ct));
    }

    [Theory]
    [InlineData(700_111L, null, "x")]
    [InlineData(700_112L, "nonsense", "x")]
    [InlineData(700_113L, "other", "TOO_LONG")]
    public async Task ReportPost_InvalidInput_RedirectsWithInvalidAndStoresNothing(long owner, string? reason, string comment)
    {
        using HttpClient client = await SignedInAsync(owner);
        string text = comment == "TOO_LONG" ? new string('x', 1001) : comment;

        using HttpResponseMessage response = await PostAsync(client, Path, await ReportFormAsync(client, Path, reason, text));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.EndsWith(Path + "?feedback=invalid" + Anchor, response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await ReportsAsync(owner));
    }

    [Fact]
    public async Task QuestionPage_SignedInAfterAnonymousWarmUp_GetsFormsAndNoStore()
    {
        const long Owner = 700_109L;
        using HttpClient anonymous = Bare(site.Factory);
        (HttpResponseMessage warm, _) = await GetAsync(anonymous, Path);
        (HttpResponseMessage hit, string anonymousBody) = await GetAsync(anonymous, Path);
        using (warm)
        using (hit)
        {
            Assert.True(IsHit(hit), "The anonymous page should be cached before the signed-in request.");
            Assert.DoesNotContain("id=\"feedback\"", anonymousBody, StringComparison.Ordinal);
        }

        using HttpClient client = await SignedInAsync(Owner);
        (HttpResponseMessage signedIn, string body) = await GetAsync(client, Path);
        using (signedIn)
        {
            Assert.False(IsHit(signedIn), "A signed-in request must never be served the cached anonymous page.");
            Assert.Contains("id=\"feedback\"", body, StringComparison.Ordinal);
            Assert.Contains("__RequestVerificationToken", HiddenFieldsOf(body, "question-rating").Keys);
            string cacheControl = string.Join(", ", signedIn.Headers.GetValues("Cache-Control"));
            Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
            Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RatingPost_OverTheWriteLimit_RedirectsWithRateLimitedNoticeAndStoresNothingMore()
    {
        const long Owner = 700_114L;
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = telegram,
            Settings = new Dictionary<string, string?> { ["App:RateLimits:WritesPerMinute"] = "2" },
        });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = await SignInAsync(factory, telegram, Owner, Ct);

        using HttpResponseMessage first = await PostAsync(client, Path, await RatingFormAsync(client, Path, "like"));
        using HttpResponseMessage second = await PostAsync(client, Path, await RatingFormAsync(client, Path, "clear"));
        using HttpResponseMessage third = await PostAsync(client, Path, await RatingFormAsync(client, Path, "dislike"));

        Assert.EndsWith("?feedback=rated" + Anchor, first.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.EndsWith("?feedback=cleared" + Anchor, second.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Found, third.StatusCode);
        Assert.EndsWith(Path + "?feedback=rate-limited" + Anchor, third.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(
            factory,
            $"""SELECT count(*)::int AS "Value" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {Owner}""",
            Ct));

        (HttpResponseMessage page, string body) = await GetAsync(client, Path + "?feedback=rate-limited");
        using (page)
        {
            Assert.Contains("Too many requests. Try again in a minute.", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnonymousPageAfterSignedInPost_StillServedFromCacheWithoutForms()
    {
        const long Owner = 700_108L;
        using HttpClient anonymous = Bare(site.Factory);
        (HttpResponseMessage warm, _) = await GetAsync(anonymous, Path);
        warm.Dispose();
        using HttpClient client = await SignedInAsync(Owner);

        using HttpResponseMessage post = await PostAsync(client, Path, await RatingFormAsync(client, Path, "dislike"));
        Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        (HttpResponseMessage signedIn, _) = await GetAsync(client, Path + "?feedback=rated");
        signedIn.Dispose();

        (HttpResponseMessage after, string body) = await GetAsync(anonymous, Path);
        using (after)
        {
            Assert.True(IsHit(after), "The anonymous page should still come from the cache.");
            Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("id=\"feedback\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("aria-pressed", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Thank you, your rating is saved.", body, StringComparison.Ordinal);
        }
    }
}
