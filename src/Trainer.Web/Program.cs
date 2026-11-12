using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Infrastructure.Content;
using Trainer.Domain.Keys;
using Trainer.Infrastructure;
using Trainer.Web.Caching;
using Trainer.Web.Components;
using Trainer.Web.Components.Layout;
using Trainer.Web.Endpoints;
using Trainer.Web.Seo;
using Trainer.Web.ContentState;
using Trainer.Web.Routing;
using Trainer.Web.Startup;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

AppOptions appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
StartupMigrations.EnsureAllowed(appOptions, builder.Environment);
PublicBaseUrlValidation.EnsureValid(appOptions, builder.Environment);
TrustedProxies trustedProxies = ForwardedHeadersSetup.Parse(appOptions.ForwardedHeaders);
string? defaultedAllowedHost = AllowedHostsDefault.Apply(builder.Configuration, appOptions, builder.Environment);

string connectionString = builder.Configuration.GetConnectionString("Trainer")
    ?? throw new InvalidOperationException("ConnectionStrings:Trainer is not configured.");

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<ForwardedHeadersOptions>(options => ForwardedHeadersSetup.Apply(options, trustedProxies));
builder.Services.AddTrainerInfrastructure(connectionString);
builder.Services.AddScoped<IContentImportService>(provider => new ReadinessReportingImportService(
    ActivatorUtilities.CreateInstance<ContentImportService>(provider),
    provider.GetRequiredService<ReadinessState>(),
    provider.GetRequiredService<ContentSnapshotProvider>(),
    provider.GetRequiredService<ILogger<ReadinessReportingImportService>>()));
builder.Services.AddSingleton<ContentSnapshotProvider>();
builder.Services.AddSingleton<IContentCacheInvalidator, OutputCacheInvalidator>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<SiteUrls>();
builder.Services.AddScoped<PageLinks>();
builder.Services.AddHostedService<ContentStartupImporter>();
builder.Services.AddOutputCache(options =>
{
    options.SizeLimit = 100L * 1024 * 1024;
    options.MaximumBodySize = 2L * 1024 * 1024;
    options.AddPolicy(PublicPageCachePolicy.PolicyName, new PublicPageCachePolicy(varyByCatalogQuery: false));
    options.AddPolicy(PublicPageCachePolicy.CatalogPolicyName, new PublicPageCachePolicy(varyByCatalogQuery: true));
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddLocalization(options => options.ResourcesPath = "Localization/Resources");
builder.Services.AddRazorComponents();
// The default encoder writes every non-Latin character as a numeric entity, which triples the size of Russian pages.
builder.Services.AddWebEncoders(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic));
builder.Services.AddHealthChecks()
    .AddCheck<ReadinessHealthCheck>("content", HealthStatus.Unhealthy, [ReadinessHealthCheck.Tag]);

WebApplication app = builder.Build();

if (defaultedAllowedHost is not null)
{
    AllowedHostsDefault.LogApplied(app.Logger, defaultedAllowedHost);
}

if (appOptions.ApplyMigrationsOnStartup)
{
    await StartupMigrations.ApplyAsync(app.Services, app.Lifetime.ApplicationStopping);
}

// First: everything after it (cookie flags, redirects, logging) must see the client address and scheme, not the proxy's.
app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
// Outside the output cache: entries are stored uncompressed and encoded per request.
app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRouting();
app.UseMiddleware<CultureAndStackMiddleware>();
app.UseRequestLocalization(options =>
{
    CultureKey[] cultures = [.. CultureKey.Supported];
    options.DefaultRequestCulture = new RequestCulture(CultureKey.Default.Value);
    options.SupportedCultures = [.. cultures.Select(c => new System.Globalization.CultureInfo(c.Value))];
    options.SupportedUICultures = options.SupportedCultures;
    options.RequestCultureProviders = [new RouteCultureProvider()];
});
app.UseOutputCache();
app.UseAntiforgery();

app.MapStaticAssets();
RootRedirectEndpoint.Map(app);
LanguageEndpoint.Map(app);
SitemapEndpoints.Map(app);
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains(ReadinessHealthCheck.Tag) });
app.MapRazorComponents<App>();

app.Run();

public partial class Program;
