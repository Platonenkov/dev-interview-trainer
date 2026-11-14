namespace Trainer.Web.Auth;

/// <summary>Fails startup on a Telegram OIDC configuration that would be unsafe or silently broken.</summary>
public static class TelegramOidcStartupCheck
{
    public const string ClientIdKey = "TelegramOidc:ClientId";
    public const string ClientSecretKey = "TelegramOidc:ClientSecret";

    /// <summary>Points the OIDC handler at the fake provider; honoured only in the Testing environment.</summary>
    public const string MetadataAddressKey = "TelegramOidc:MetadataAddress";

    public const string TestingEnvironment = "Testing";

    /// <exception cref="InvalidOperationException">
    /// Production lacks the client id or secret (sign-in would be unreachable), or a metadata address is set outside
    /// Testing (it would let configuration redirect token validation to another issuer).
    /// </exception>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsProduction()
            && (string.IsNullOrWhiteSpace(configuration[ClientIdKey]) || string.IsNullOrWhiteSpace(configuration[ClientSecretKey])))
        {
            throw new InvalidOperationException(
                $"{ClientIdKey} and {ClientSecretKey} are required in Production; set them through environment variables.");
        }

        if (!string.IsNullOrWhiteSpace(configuration[MetadataAddressKey]) && !environment.IsEnvironment(TestingEnvironment))
        {
            throw new InvalidOperationException(
                $"{MetadataAddressKey} is only allowed in the {TestingEnvironment} environment, where it points at the fake provider.");
        }
    }
}
