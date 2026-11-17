using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Telegram.OpenIdConnect.AspNetCore;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Application.Feedback;
using Trainer.Infrastructure.Content;
using Trainer.Domain.Keys;
using Trainer.Infrastructure;
using Trainer.Infrastructure.Persistence;
using Trainer.Contracts;
using Trainer.Web.Analytics;
using Trainer.Web.Api;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using Trainer.Web.Components;
using Trainer.Web.Components.Layout;
using Trainer.Web.Endpoints;
using Trainer.Web.Internal;
using Trainer.Web.Islands;
using Trainer.Web.OpenGraph;
using Trainer.Web.Seo;
using Trainer.Web.ContentState;
using Trainer.Web.Routing;
using Trainer.Web.Security;
using Trainer.Web.Startup;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

AppOptions appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
StartupMigrations.EnsureAllowed(appOptions, builder.Environment);
PublicBaseUrlValidation.EnsureValid(appOptions, builder.Environment);
TrustedProxies trustedProxies = ForwardedHeadersSetup.Parse(appOptions.ForwardedHeaders);
string? defaultedAllowedHost = AllowedHostsDefault.Apply(builder.Configuration, appOptions, builder.Environment);
RateLimitOptions rateLimits = ApiRateLimits.Read(builder.Configuration);
FeedbackOptions feedbackOptions = FeedbackSetup.Read(builder.Configuration);
AnalyticsOptions analyticsOptions = AnalyticsSetup.Read(builder.Configuration);
InternalOptions internalOptions = builder.Configuration.GetSection(InternalOptions.SectionName).Get<InternalOptions>() ?? new InternalOptions();
InternalStartupCheck.EnsureValid(internalOptions, builder.Configuration[InternalOptions.TokenKey], builder.Environment);

string connectionString = builder.Configuration.GetConnectionString("Trainer")
    ?? throw new InvalidOperationException("ConnectionStrings:Trainer is not configured.");

// The library's own category logs the Telegram username and the client IP; SignInLogSink is the only sign-in log.
builder.Logging.AddSignInLogRules();

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<InternalOptions>(builder.Configuration.GetSection(InternalOptions.SectionName));
builder.Services.Configure<OpenGraphOptions>(builder.Configuration.GetSection(OpenGraphOptions.SectionName));
builder.Services.Configure<ForwardedHeadersOptions>(options => ForwardedHeadersSetup.Apply(options, trustedProxies));
builder.Services.AddTrainerInfrastructure(connectionString);
builder.Services.AddTrainerFeedback(feedbackOptions);
builder.Services.AddTrainerAnalytics(analyticsOptions);
builder.Services.AddScoped<IContentImportService>(provider => new ReadinessReportingImportService(
    ActivatorUtilities.CreateInstance<ContentImportService>(provider),
    provider.GetRequiredService<ReadinessState>(),
    provider.GetRequiredService<ContentSnapshotProvider>(),
    provider.GetRequiredService<ILogger<ReadinessReportingImportService>>()));
builder.Services.AddSingleton<ContentSnapshotProvider>();
builder.Services.AddSingleton<OutputCacheInvalidator>();
builder.Services.AddSingleton<OgImageCache>();
builder.Services.AddSingleton<OgCardRenderer>();
builder.Services.AddSingleton<OgImageLinks>();
// Importing clears the page cache and the rendered Open Graph images together.
builder.Services.AddSingleton<IContentCacheInvalidator, CompositeContentCacheInvalidator>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<SiteUrls>();
builder.Services.AddScoped<PageLinks>();
builder.Services.AddScoped<IslandBootstrapFactory>();
builder.Services.AddScoped<CspNonce>();
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
builder.Services.AddTrainerAuth(builder.Configuration, builder.Environment);
// Auth and antiforgery cookies are protected with this key ring; it lives in PostgreSQL so it survives restarts (ADR 0004).
builder.Services.AddDataProtection()
    .SetApplicationName("dev-interview-trainer")
    .PersistKeysToDbContext<TrainerDbContext>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AntiforgeryNames.Header;
    options.Cookie.Name = AntiforgeryNames.Cookie;
});
// Source-generated metadata first, so /api bodies never need reflection (the WASM client uses the same context).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, TrainerJsonContext.Default));
builder.Services.AddTrainerApiRateLimits(rateLimits);
builder.Services.AddLocalization(options => options.ResourcesPath = "Localization/Resources");
// Islands are WebAssembly only (spec 8.2); AddInteractiveServerComponents is never called.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(options => options.SerializationCallback = AuthenticationStateSerialization.SerializeAsync);
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
// After the security headers so its 503 carries them, and still before routing, so no write endpoint is reachable.
app.UseMiddleware<MaintenanceGateMiddleware>();
// Outside the output cache: entries are stored uncompressed and encoded per request.
app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseMiddleware<StatusPageAsGetMiddleware>();
// Before routing, so it also wraps authentication, authorization, rate limiting, binding and unmatched /api paths.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(ApiEndpoints.Prefix, StringComparison.OrdinalIgnoreCase),
    api => api.UseMiddleware<ApiErrorMiddleware>());
// Before authentication, so its start-of-response callback runs after the cookie handler's and can strip a renewed cookie.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(OgImageLinks.Prefix, StringComparison.OrdinalIgnoreCase),
    og => og.UseMiddleware<OgResponseHeadersMiddleware>());
app.UseRouting();
// Before authentication on purpose: its OnStarting callback must be registered earlier than the cookie handler's, so it
// runs after it and the handler's own no-cache headers on a reissued cookie cannot replace private, no-store.
app.UseMiddleware<PrivateResponseMiddleware>();
// Before the culture check: the OIDC callback /signin-telegram also matches the home page's /{Culture} route, which
// that middleware would answer with 404 before the handler could take the callback.
app.UseAuthentication();
// Routing has matched the endpoint and the compression middleware has installed its feature.
app.UseMiddleware<BreachGuardMiddleware>();
app.UseMiddleware<CultureAndStackMiddleware>();
app.UseRequestLocalization(options =>
{
    CultureKey[] cultures = [.. CultureKey.Supported];
    options.DefaultRequestCulture = new RequestCulture(CultureKey.Default.Value);
    options.SupportedCultures = [.. cultures.Select(c => new System.Globalization.CultureInfo(c.Value))];
    options.SupportedUICultures = options.SupportedCultures;
    options.RequestCultureProviders = [new RouteCultureProvider()];
});
app.UseAuthorization();
// After authentication and authorization: partitions are per user, and anonymous calls to signed-in endpoints are not counted.
app.UseRateLimiter();
// After authentication, so PublicPageCachePolicy sees the signed-in user and bypasses the cache.
app.UseOutputCache();
app.UseAntiforgery();

app.MapStaticAssets();
RootRedirectEndpoint.Map(app);
LanguageEndpoint.Map(app);
SignInRedirectEndpoint.Map(app);
app.MapTelegramOidcEndpoints();
app.MapTrainerApi();
SitemapEndpoints.Map(app);
OgImageEndpoints.Map(app);
InternalEndpoints.Map(app, mapEndpoints: internalOptions.Port > 0);

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains(ReadinessHealthCheck.Tag) });
RazorComponentsEndpointConventionBuilder pages = app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode();
pages.Add(IslandEndpointConventions.KeepRenderModesOnIslandPagesOnly);
pages.Add(IslandEndpointConventions.KeepResourceCollectionOnServer);

app.Run();

public partial class Program;
