using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.Web.Auth;
using Trainer.Web.Caching;

namespace Trainer.TestSupport;

/// <summary>
/// Reads the session ticket from a response's <c>Set-Cookie</c> headers with the app's own ticket data format, so tests
/// assert on the claims the app issued without the app exposing a debugging endpoint.
/// </summary>
public static class SessionCookieReader
{
    private const string CookieName = AuthCookieNames.Session;
    private const string SchemeName = TelegramAuth.CookieScheme;
    private const string ChunksPrefix = "chunks-";

    /// <summary>The issued principal, or null when the response sets no session cookie (or deletes it).</summary>
    public static ClaimsPrincipal? Read(TrainerWebFactory factory, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Read(factory.Services, response);
    }

    /// <summary>The issued authentication properties (persistence, expiry), or null when no session cookie is set.</summary>
    public static AuthenticationProperties? ReadProperties(TrainerWebFactory factory, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return ReadProperties(factory.Services, response);
    }

    public static ClaimsPrincipal? Read(IServiceProvider services, HttpResponseMessage response) =>
        ReadTicket(services, response)?.Principal;

    public static AuthenticationProperties? ReadProperties(IServiceProvider services, HttpResponseMessage response) =>
        ReadTicket(services, response)?.Properties;

    private static AuthenticationTicket? ReadTicket(IServiceProvider services, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(response);

        string? protectedTicket = ReadCookieValue(response);
        if (protectedTicket is null)
        {
            return null;
        }

        CookieAuthenticationOptions options = services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SchemeName);
        return options.TicketDataFormat.Unprotect(protectedTicket);
    }

    private static string? ReadCookieValue(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? headers))
        {
            return null;
        }

        Dictionary<string, string> cookies = new(StringComparer.Ordinal);
        foreach (string header in headers)
        {
            string pair = header.Split(';', 2)[0];
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                cookies[pair[..separator].Trim()] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        if (!cookies.TryGetValue(CookieName, out string? value) || value.Length == 0)
        {
            return null;
        }

        // Tickets over the single-cookie limit are split into <name>C1..CN, with "chunks-N" left in the main cookie.
        if (value.StartsWith(ChunksPrefix, StringComparison.Ordinal)
            && int.TryParse(value[ChunksPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            System.Text.StringBuilder joined = new();
            for (int i = 1; i <= count; i++)
            {
                if (!cookies.TryGetValue($"{CookieName}C{i}", out string? chunk))
                {
                    return null;
                }

                joined.Append(chunk);
            }

            return joined.ToString();
        }

        return value;
    }
}
