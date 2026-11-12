namespace Trainer.Web.Startup;

public static partial class AllowedHostsDefault
{
    public const string ConfigurationKey = "AllowedHosts";

    /// <summary>
    /// Outside Development and Testing, replaces a wildcard or empty <c>AllowedHosts</c> with the host of
    /// <c>App:PublicBaseUrl</c> plus the loopback names, so host filtering rejects requests for any other name instead of accepting everything.
    /// An explicitly configured value other than <c>*</c> always wins. Must run before the host is built, because
    /// host filtering reads the setting when its middleware is created.
    /// </summary>
    /// <returns>The host that was applied, or <see langword="null"/> when the configuration was left alone.</returns>
    public static string? Apply(IConfigurationManager configuration, AppOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return null;
        }

        string? configured = configuration[ConfigurationKey];
        bool isWildcard = string.IsNullOrWhiteSpace(configured) || string.Equals(configured.Trim(), "*", StringComparison.Ordinal);
        if (!isWildcard || !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? publicBase))
        {
            return null;
        }

        // Loopback names stay allowed because container healthchecks and local probes call the app on localhost; they
        // cannot be abused for cache poisoning, since canonical URLs come from PublicBaseUrl and not from the Host header.
        string allowed = string.Join(';', publicBase.Host, "localhost", "127.0.0.1", "[::1]");
        configuration[ConfigurationKey] = allowed;
        return allowed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AllowedHosts was not restricted; using the host of App:PublicBaseUrl: {Host}.")]
    public static partial void LogApplied(ILogger logger, string host);
}
