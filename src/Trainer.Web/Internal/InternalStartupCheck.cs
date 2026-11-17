namespace Trainer.Web.Internal;

/// <summary>Fails startup on a configuration that would leave the import endpoint unreachable or open to a guessable token.</summary>
public static class InternalStartupCheck
{
    /// <summary>The shortest accepted import token; 32 characters is 128 bits when generated as hex.</summary>
    public const int MinimumTokenLength = 32;

    /// <exception cref="InvalidOperationException">
    /// The port is set and the token is shorter than <see cref="MinimumTokenLength"/>, or Production runs without a port or
    /// without a token (a deployment that cannot receive content is a configuration error, not a silent fallback).
    /// </exception>
    public static void EnsureValid(InternalOptions options, string? token, IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(env);

        if (env.IsProduction())
        {
            if (options.Port <= 0)
            {
                throw new InvalidOperationException(
                    $"{InternalOptions.SectionName}:Port must be a positive port in Production; the content import endpoint is part of the deployment.");
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"{InternalOptions.TokenKey} is required in Production; set it through an environment variable.");
            }
        }

        if (options.Port > 0 && (string.IsNullOrWhiteSpace(token) || token.Length < MinimumTokenLength))
        {
            throw new InvalidOperationException(
                $"{InternalOptions.TokenKey} must be at least {MinimumTokenLength} characters when {InternalOptions.SectionName}:Port is set.");
        }

        // The bearer comparison does not trim, so a token padded with whitespace (a trailing newline in an env file) could never match.
        if (token is not null && !string.Equals(token, token.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{InternalOptions.TokenKey} must not start or end with whitespace.");
        }
    }
}
