using System.Buffers.Text;
using System.Security.Cryptography;

namespace Trainer.Web.Startup;

/// <summary>
/// The CSP nonce of one island page response (ADR 0012). It allows exactly one inline script, the framework's import map,
/// whose content changes with every build (it maps the runtime's plain module names to fingerprinted files), so a fixed
/// hash cannot allow it. Island pages are never cached, so no nonce is ever served twice.
/// </summary>
public sealed class CspNonce
{
    private string? _value;

    /// <summary>
    /// 144 random bits as 24 base64url characters, created on first use and fixed for the rest of the request. The URL-safe
    /// alphabet is valid in a CSP nonce and has no <c>+</c>, which the HTML encoder would write as an entity.
    /// </summary>
    public string Value => _value ??= Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
}
