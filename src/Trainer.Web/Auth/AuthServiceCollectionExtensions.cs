using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Telegram.OpenIdConnect.AspNetCore;
using Trainer.Application.Users;
using Trainer.Domain.Users;
using Trainer.Web.Caching;

namespace Trainer.Web.Auth;

public static class AuthServiceCollectionExtensions
{
    public const string AdminPolicy = "Admin";

    private const int MaxErrorCodeLength = 64;

    /// <summary>
    /// Telegram sign-in through <c>Telegram.OpenIdConnect.AspNetCore</c>, the database-backed gate, the sign-in log,
    /// the session cookie settings, the admin list and the authorization policies.
    /// </summary>
    public static IServiceCollection AddTrainerAuth(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        TelegramOidcStartupCheck.Validate(configuration, environment);
        services.AddSingleton(Options.Create(new AccessOptions { AdminTelegramIds = AdminTelegramIds.Parse(configuration) }));

        services.AddTelegramOidc(options =>
        {
            options.ClientId = configuration[TelegramOidcStartupCheck.ClientIdKey] ?? string.Empty;
            options.ClientSecret = configuration[TelegramOidcStartupCheck.ClientSecretKey] ?? string.Empty;
            options.CookieScheme = TelegramAuth.CookieScheme;
            options.OidcScheme = TelegramAuth.OidcScheme;
            options.CallbackPath = TelegramAuth.CallbackPath;
            options.LoginPath = TelegramAuth.LoginPath;
            options.AccessDeniedPath = TelegramAuth.AccessDeniedPath;
            options.CookieTtlHours = TelegramAuth.CookieTtlHours;
            options.DefaultReturnUrl = "/";
        });

        // The startup check has already refused a metadata address outside Testing. The fake provider is plain http
        // on loopback, so the handler's https-only metadata rule is lifted there and nowhere else.
        if (environment.IsEnvironment(TelegramOidcStartupCheck.TestingEnvironment)
            && configuration[TelegramOidcStartupCheck.MetadataAddressKey] is { Length: > 0 } metadataAddress)
        {
            services.Configure<OpenIdConnectOptions>(TelegramAuth.OidcScheme, options =>
            {
                options.MetadataAddress = metadataAddress;
                options.RequireHttpsMetadata = false;
            });
        }

        // Runs after the library's configuration, which leaves OnMessageReceived and OnTicketReceived unset.
        services.Configure<OpenIdConnectOptions>(TelegramAuth.OidcScheme, options =>
        {
            options.Events.OnMessageReceived = context =>
            {
                ScrubAuthorizationError(context.ProtocolMessage);
                return Task.CompletedTask;
            };

            // The library's challenge endpoint is public and its local-URL check lets control characters through
            // ("/\t/evil.test", which a browser reads as "//evil.test"), so the final target is validated again here.
            options.Events.OnTicketReceived = context =>
            {
                context.ReturnUri = SignInRedirectEndpoint.ResolveReturnPathAfterSignIn(context.ReturnUri);
                return Task.CompletedTask;
            };
        });

        services.AddSingleton<UserClaimsFactory>();
        services.AddScoped<ITelegramSignInGate, TrainerSignInGate>();
        services.AddScoped<ITelegramSignInAuditSink, SignInLogSink>();
        services.AddScoped<TrainerCookieEvents>();

        // Registered after AddTelegramOidc so it runs after the library's cookie configuration; the library has no
        // cookie-name option (ADR 0002).
        services.AddOptions<CookieAuthenticationOptions>(TelegramAuth.CookieScheme)
            .Configure<TimeProvider>((options, time) =>
            {
                options.Cookie.Name = AuthCookieNames.Session;
                options.EventsType = typeof(TrainerCookieEvents);
                // The handler reads its own clock from the options, not from DI; expiry and sliding renewal must
                // agree with the revalidation timer in TrainerCookieEvents.
                options.TimeProvider = time;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(Roles.Admin));
        services.AddCascadingAuthenticationState();
        return services;
    }

    /// <summary>
    /// The OIDC handler logs an error response at Error level, verbatim, before the remote-failure event runs, and
    /// puts the same text into the failure exception; only the message-received event comes earlier.
    /// <c>error_description</c> and <c>error_uri</c> are free text from the provider (or whoever forged the callback),
    /// so they are dropped, and an <c>error</c> that is not a plain OAuth code is replaced; the failure itself still
    /// happens and still reaches the sign-in log as a reason code.
    /// </summary>
    private static void ScrubAuthorizationError(OpenIdConnectMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Error))
        {
            return;
        }

        message.ErrorDescription = null;
        message.ErrorUri = null;
        if (message.Error.Length > MaxErrorCodeLength || !message.Error.All(c => c is (>= 'a' and <= 'z') or '_'))
        {
            message.Error = "invalid_error";
        }
    }
}
