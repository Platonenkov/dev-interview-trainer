namespace Trainer.Web.Startup;

public static class PublicBaseUrlValidation
{
    /// <summary>
    /// Outside Development and Testing the public origin must be configured as an absolute https URL: with output caching,
    /// a request-derived origin would let a spoofed Host header poison cached canonical, hreflang and sitemap URLs.
    /// </summary>
    public static void EnsureValid(AppOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"App:PublicBaseUrl must be an absolute https URL in the {environment.EnvironmentName} environment (current: '{options.PublicBaseUrl}').");
        }
    }
}
