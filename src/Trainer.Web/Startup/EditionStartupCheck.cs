namespace Trainer.Web.Startup;

public static class EditionStartupCheck
{
    public const string StaticExportEnvironment = "StaticExport";

    /// <summary>
    /// The static edition drops accounts and the API, so a live deployment running it would silently serve a degraded
    /// site. It is allowed only in the export environment and in local runs and tests.
    /// </summary>
    public static void EnsureValid(AppOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (options.Edition != SiteEdition.Static)
        {
            return;
        }

        if (environment.IsEnvironment(StaticExportEnvironment) || environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        throw new InvalidOperationException(
            $"App:Edition=Static is allowed only in the {StaticExportEnvironment}, Development and Testing environments (current: {environment.EnvironmentName}).");
    }
}
