using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trainer.Application.Progress;
using Trainer.Domain.Grading;
using Trainer.TestSupport;
using Trainer.Web.Components.Shared;

namespace Trainer.Web.Tests;

public sealed class StatusChipTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    [Theory]
    [InlineData("ru", 1, "через 1 день")]
    [InlineData("ru", 2, "через 2 дня")]
    [InlineData("ru", 4, "через 4 дня")]
    [InlineData("ru", 5, "через 5 дней")]
    [InlineData("ru", 11, "через 11 дней")]
    [InlineData("ru", 14, "через 14 дней")]
    [InlineData("ru", 21, "через 21 день")]
    [InlineData("ru", 22, "через 22 дня")]
    [InlineData("ru", 25, "через 25 дней")]
    [InlineData("ru", 111, "через 111 дней")]
    [InlineData("en", 1, "in 1 day")]
    [InlineData("en", 2, "in 2 days")]
    [InlineData("en", 21, "in 21 days")]
    public async Task InDays_UsesThePluralFormOfTheCulture(string culture, int days, string expected)
    {
        TimeProvider clock = site.Factory.Services.GetRequiredService<TimeProvider>();
        // A minute short of the whole number of days, so the rounding up lands on `days` whatever the test's own delay.
        QuestionStatus status = new(Grade.Knew, clock.GetUtcNow().AddDays(days).AddMinutes(-1), IsDue: false);
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
            await using HtmlRenderer renderer = new(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

            string html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                HtmlRootComponent root = await renderer.RenderComponentAsync<StatusChip>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(StatusChip.Status)] = status }));
                return root.ToHtmlString();
            });

            Assert.Contains(expected, html, StringComparison.Ordinal);
            Assert.Matches(@"<span class=""visually-hidden"">" + (culture == "ru" ? "Статус:" : "Status:") + @"\s*</span>", html);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
