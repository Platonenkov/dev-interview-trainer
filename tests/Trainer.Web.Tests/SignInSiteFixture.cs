using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Trainer.TestSupport;
using Trainer.Web.Caching;

namespace Trainer.Web.Tests;

/// <summary>One imported site wired to its own fake Telegram provider, with one configured admin and captured logs.</summary>
public sealed class SignInSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const long AdminTelegramId = 777000;

    public FakeTelegramOidcServer Telegram { get; private set; } = null!;

    public TrainerWebFactory Factory { get; private set; } = null!;

    public CapturingLoggerProvider Logs { get; } = new();

    public async ValueTask InitializeAsync()
    {
        Telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        Factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = Telegram,
            Logs = Logs,
            Settings = new Dictionary<string, string?>
            {
                ["App:AdminTelegramIds"] = AdminTelegramId.ToString(CultureInfo.InvariantCulture) + ",777001",
            },
        });
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Telegram.DisposeAsync();
    }
}

/// <summary>Small helpers shared by the sign-in tests.</summary>
public static partial class SignInTestHelpers
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryField();

    /// <summary>The <c>Set-Cookie</c> header that sets <paramref name="name"/>, or null.</summary>
    public static string? SetCookie(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
    }

    /// <summary>The cookie value from a <c>Set-Cookie</c> header (still URL-encoded, as a browser sends it back).</summary>
    public static string CookieValue(string setCookie)
    {
        ArgumentNullException.ThrowIfNull(setCookie);
        string pair = setCookie.Split(';', 2)[0];
        return pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..];
    }

    /// <summary>The <c>Location</c> header exactly as sent, including characters <see cref="Uri"/> would reject or alter.</summary>
    public static string? RawLocation(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.NonValidated.TryGetValues("Location", out HeaderStringValues values) ? values.ToString() : null;
    }

    public static string SessionCookieValue(HttpResponseMessage response) =>
        CookieValue(SetCookie(response, AuthCookieNames.Session) ?? throw new InvalidOperationException("No session cookie was issued."));

    public static string AntiforgeryToken(string html)
    {
        Match match = AntiforgeryField().Match(html);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["token"].Value) : throw new InvalidOperationException("No antiforgery field.");
    }

    /// <summary>A client that keeps cookies and does not follow redirects, as the sign-in driver requires.</summary>
    public static HttpClient Browser(TrainerWebFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateNonRedirectingClient();
    }

    /// <summary>A client without a cookie jar, for requests that send a hand-picked <c>Cookie</c> header.</summary>
    public static HttpClient Bare(TrainerWebFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }
}
