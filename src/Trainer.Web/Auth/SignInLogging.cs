namespace Trainer.Web.Auth;

/// <summary>
/// Log rules that keep sign-in personal data and one-time OIDC values out of every logging provider, whatever the
/// configured levels are.
/// </summary>
public static class SignInLogging
{
    /// <summary>
    /// The Telegram library's category, which logs the username and the client IP, is off. The OIDC handler and the
    /// request diagnostics log the challenge and callback URLs (authorization code, state, nonce) below Warning, so
    /// they are held at Warning.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, LogLevel>> Ceilings =
    [
        new("TelegramOidc", LogLevel.None),
        new("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Warning),
        new("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning),
    ];

    /// <summary>
    /// Adds the rules for every provider. A provider-agnostic rule alone is not enough: the rule selector prefers any
    /// rule that names the provider (for example <c>Logging:Console:LogLevel:Default</c>) over every rule that does not,
    /// so the same rules are repeated for each provider named in the configured rules, after configuration binding.
    /// Full category names leave no longer prefix that could outrank them, and on a tie the later rule wins.
    /// </summary>
    public static ILoggingBuilder AddSignInLogRules(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        foreach (KeyValuePair<string, LogLevel> ceiling in Ceilings)
        {
            logging.AddFilter(ceiling.Key, ceiling.Value);
        }

        logging.Services.PostConfigure<LoggerFilterOptions>(AddPerProviderRules);
        return logging;
    }

    private static void AddPerProviderRules(LoggerFilterOptions options)
    {
        string[] providers = [.. options.Rules.Select(rule => rule.ProviderName).OfType<string>().Distinct(StringComparer.Ordinal)];
        foreach (string provider in providers)
        {
            foreach (KeyValuePair<string, LogLevel> ceiling in Ceilings)
            {
                options.Rules.Add(new LoggerFilterRule(provider, ceiling.Key, ceiling.Value, filter: null));
            }
        }
    }
}
