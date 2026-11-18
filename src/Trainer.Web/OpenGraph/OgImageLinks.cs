using System.Globalization;
using Microsoft.Extensions.Localization;
using Trainer.Application.Questions;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Web.Seo;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// Builds the cards and the URLs of their images. The page that advertises an image and the endpoint that serves it
/// both ask this class, so they cannot disagree about what the current fingerprint is.
/// </summary>
public sealed class OgImageLinks(IStringLocalizer<SharedResource> localizer, SiteUrls urls)
{
    public const string Prefix = "/og";
    public const string SiteSegment = "site";

    public static string QuestionPath(CultureKey culture, string stack, string slug, string fingerprint) =>
        $"{Prefix}/{culture.Value}/{stack}/{slug}/{fingerprint}.png";

    public static string SitePath(CultureKey culture, string fingerprint) =>
        $"{Prefix}/{culture.Value}/{SiteSegment}/{fingerprint}.png";

    public OgCard SiteCard(CultureKey culture, HttpRequest? request) =>
        InCulture(culture, () => new OgCard(culture.Value, Text("Home.Tagline"), null, null, Footer(request)));

    public OgCard QuestionCard(CultureKey culture, QuestionPageModel question, HttpRequest? request)
    {
        ArgumentNullException.ThrowIfNull(question);
        return InCulture(culture, () => new OgCard(culture.Value, question.Title, LevelKeys.ToKey(question.Level), question.TopicTitle, Footer(request)));
    }

    /// <summary>Absolute URL of the site card for the culture.</summary>
    public string SiteUrl(CultureKey culture, HttpRequest? request) =>
        urls.AbsoluteAsset(request, SitePath(culture, OgFingerprint.Of(SiteCard(culture, request))));

    /// <summary>Absolute URL of the card of a live question; a retired one has no card (the endpoint answers 404).</summary>
    public string QuestionUrl(CultureKey culture, QuestionPageModel question, HttpRequest? request)
    {
        ArgumentNullException.ThrowIfNull(question);
        string fingerprint = OgFingerprint.Of(QuestionCard(culture, question, request));
        return urls.AbsoluteAsset(request, QuestionPath(culture, question.Stack.Value, question.Slug, fingerprint));
    }

    private string Footer(HttpRequest? request)
    {
        string origin = urls.Origin(request);
        string host = Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ? uri.Host : request?.Host.Host ?? string.Empty;
        string name = Text("Site.Name");
        return host.Length == 0 ? name : $"{name} · {host}";
    }

    private string Text(string key) => localizer[key].Value;

    // The localizer reads CurrentUICulture, and the image endpoint runs under the default culture, not the card's.
    // The action must stay synchronous: CurrentUICulture is async-local, so an await inside it could resume with the
    // temporary culture still set or already restored.
    private static T InCulture<T>(CultureKey culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture.Value);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
