using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Trainer.Web.Internal;

/// <summary>
/// Requires <c>Authorization: Bearer &lt;token&gt;</c> equal to <c>ContentImport:Token</c>. The comparison is over UTF-8 bytes
/// in fixed time; a presented token of another length is compared against a dummy of its own length, so the response
/// time does not tell how long the real token is. Nothing here logs the header.
/// </summary>
public sealed class ImportTokenFilter(IConfiguration configuration) : IEndpointFilter
{
    private const string Scheme = "Bearer";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        string? expected = configuration[InternalOptions.TokenKey];
        string? presented = ReadBearer(context.HttpContext.Request);
        if (!string.IsNullOrEmpty(expected) && presented is not null && Matches(expected, presented))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(new UnauthorizedResult());
    }

    private static string? ReadBearer(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // No trimming: the configured token is compared as is (the startup check rejects padded ones), so must the header be.
        return header[(Scheme.Length + 1)..];
    }

    private static bool Matches(string expected, string presented)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        if (expectedBytes.Length == presentedBytes.Length)
        {
            return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
        }

        _ = CryptographicOperations.FixedTimeEquals(presentedBytes, new byte[presentedBytes.Length]);
        return false;
    }

    private sealed class UnauthorizedResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers[HeaderNames.WWWAuthenticate] = Scheme;
            return Task.CompletedTask;
        }
    }
}
