namespace Trainer.Web.Startup;

public static class PublicBaseUrlValidation
{
    /// <summary>
    /// Outside Development and Testing the public origin must be configured as an absolute https URL: with output caching,
    /// a request-derived origin would let a spoofed Host header poison cached canonical, hreflang and sitemap URLs.
    /// A path is allowed (the static edition lives under <c>/{repository}/</c>), but no query, no fragment and no
    /// <c>.</c> or <c>..</c> segment, because every URL is built by prefixing the path.
    /// </summary>
    public static void EnsureValid(AppOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        bool relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        bool parsed = Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? uri);

        if (!parsed)
        {
            if (relaxed)
            {
                return;
            }

            throw MustBeHttps(options, environment);
        }

        if (!relaxed && uri!.Scheme != Uri.UriSchemeHttps)
        {
            throw MustBeHttps(options, environment);
        }

        EnsurePlainPath(options.PublicBaseUrl!, uri!);
    }

    private static InvalidOperationException MustBeHttps(AppOptions options, IHostEnvironment environment) =>
        new($"App:PublicBaseUrl must be an absolute https URL in the {environment.EnvironmentName} environment (current: '{options.PublicBaseUrl}').");

    private static void EnsurePlainPath(string configured, Uri uri)
    {
        if (uri.Query.Length > 1 || uri.Fragment.Length > 1 || configured.Contains('?', StringComparison.Ordinal) || configured.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"App:PublicBaseUrl must not contain a query or a fragment (current: '{configured}').");
        }

        // The Uri class folds dot segments away, so the raw string is the only place they can still be seen.
        int schemeEnd = configured.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return;
        }

        int pathStart = configured.IndexOf('/', schemeEnd + 3);
        if (pathStart < 0)
        {
            return;
        }

        string path = configured[pathStart..];

        // UsePathBase takes a decoded path while URLs are written encoded, so one spelling is allowed: plain ASCII.
        if (path.Any(c => c == '%' || c > 127))
        {
            throw new InvalidOperationException($"App:PublicBaseUrl path must be plain ASCII without percent-encoding (current: '{configured}').");
        }

        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException($"App:PublicBaseUrl must not contain '.' or '..' path segments (current: '{configured}').");
        }
    }
}
