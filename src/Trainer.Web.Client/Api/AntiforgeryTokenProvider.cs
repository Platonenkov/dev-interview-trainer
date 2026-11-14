using System.Net.Http.Json;
using System.Text.Json;
using Trainer.Contracts;

namespace Trainer.Web.Client.Api;

/// <summary>
/// The antiforgery request token from <c>GET /api/antiforgery</c> (spec 8.8), fetched once and reused for every write.
/// The token is bound to the signed-in user, so it is invalidated and fetched again after a 400 <c>antiforgery</c>.
/// </summary>
public sealed class AntiforgeryTokenProvider(HttpClient http)
{
    private const string Path = "api/antiforgery";

    private string? _token;

    /// <exception cref="HttpRequestException">The token could not be fetched. <see cref="HttpRequestException.StatusCode"/>
    /// is the error status, or null when no response arrived or a success response carried no token.</exception>
    public async ValueTask<string> GetAsync(CancellationToken ct)
    {
        if (_token is { } cached)
        {
            return cached;
        }

        using HttpResponseMessage response = await http.GetAsync(Path, ct);
        response.EnsureSuccessStatusCode();
        AntiforgeryTokenDto? token;
        try
        {
            token = await response.Content.ReadFromJsonAsync(TrainerJsonContext.Default.AntiforgeryTokenDto, ct);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("The antiforgery response was not a token.", exception);
        }

        if (string.IsNullOrEmpty(token?.RequestToken))
        {
            throw new HttpRequestException("The antiforgery response had no token.");
        }

        _token = token.RequestToken;
        return _token;
    }

    public void Invalidate() => _token = null;
}
