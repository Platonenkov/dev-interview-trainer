using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Trainer.Web.Startup;

/// <summary>The <c>App:ForwardedHeaders</c> configuration section: which peers may set <c>X-Forwarded-*</c>.</summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = AppOptions.SectionName + ":ForwardedHeaders";

    /// <summary>CIDR networks of trusted reverse proxies, for example <c>172.18.0.0/16</c>. Added to the loopback default.</summary>
    public string[] KnownNetworks { get; init; } = [];

    /// <summary>IP addresses of trusted reverse proxies. Added to the loopback default.</summary>
    public string[] KnownProxies { get; init; } = [];
}

/// <summary>
/// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from the configured reverse proxy only. TLS ends at the proxy,
/// so without them <c>Request.IsHttps</c> is false and the client address is the proxy's. <c>X-Forwarded-Host</c> is never
/// accepted: the real <c>Host</c> must stay what host filtering (<c>AllowedHosts</c>) judges.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const ForwardedHeaders Accepted = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    /// <summary>Parses the configured trust lists. Throws at startup so that a typo cannot silently leave the proxy untrusted.</summary>
    /// <exception cref="InvalidOperationException">An entry is not a valid CIDR network or IP address.</exception>
    public static TrustedProxies Parse(ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<IPNetwork> networks = [];
        for (int i = 0; i < settings.KnownNetworks.Length; i++)
        {
            string value = settings.KnownNetworks[i]?.Trim() ?? string.Empty;
            if (!IPNetwork.TryParse(value, out IPNetwork network))
            {
                throw new InvalidOperationException(
                    $"{ForwardedHeadersSettings.SectionName}:KnownNetworks:{i} ('{value}') is not a valid CIDR network such as 172.18.0.0/16.");
            }

            networks.Add(network);
        }

        List<IPAddress> proxies = [];
        for (int i = 0; i < settings.KnownProxies.Length; i++)
        {
            string value = settings.KnownProxies[i]?.Trim() ?? string.Empty;
            if (!IPAddress.TryParse(value, out IPAddress? address))
            {
                throw new InvalidOperationException(
                    $"{ForwardedHeadersSettings.SectionName}:KnownProxies:{i} ('{value}') is not a valid IP address.");
            }

            proxies.Add(address);
        }

        return new TrustedProxies(networks, proxies);
    }

    public static void Apply(ForwardedHeadersOptions options, TrustedProxies trusted)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trusted);

        options.ForwardedHeaders = Accepted;
        options.ForwardLimit = 1;
        foreach (IPNetwork network in trusted.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (IPAddress proxy in trusted.Proxies)
        {
            options.KnownProxies.Add(proxy);
        }
    }
}

/// <summary>Validated trust lists from configuration.</summary>
public sealed record TrustedProxies(IReadOnlyList<IPNetwork> Networks, IReadOnlyList<IPAddress> Proxies);
