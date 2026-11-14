using System.Net;
using System.Text;
using Trainer.Contracts;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// An in-process server for <see cref="Api.TrainerApiClient"/>: records every request with its headers and body and
/// answers through <see cref="Respond"/>. <c>GET /api/antiforgery</c> is answered with a numbered token unless
/// <see cref="Respond"/> handles it.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public const string AntiforgeryPath = "/api/antiforgery";

    private int _tokens;

    /// <summary>The response for a request, or null for the default (a token for the antiforgery path, else 404).</summary>
    public Func<RecordedRequest, HttpResponseMessage?> Respond { get; set; } = _ => null;

    /// <summary>Awaited before answering a recorded request, so a test can hold a response back.</summary>
    public Func<RecordedRequest, Task>? Gate { get; set; }

    /// <summary>The <c>Date</c> header of every response, when set.</summary>
    public DateTimeOffset? Date { get; set; }

    public List<RecordedRequest> Requests { get; } = [];

    public IEnumerable<RecordedRequest> Writes => Requests.Where(r => !string.Equals(r.Path, AntiforgeryPath, StringComparison.Ordinal));

    public int TokensIssued => _tokens;

    public HttpClient Client() => new(this) { BaseAddress = new Uri("http://localhost/") };

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(status, $"{{\"code\":\"{code}\",\"detail\":null}}");

    /// <summary>A 200 batch response acknowledging every id of the request: those in <paramref name="rejected"/> as rejected.</summary>
    public static HttpResponseMessage AcknowledgeAll(RecordedRequest request, params string[] rejected)
    {
        AnswerBatch batch = request.Batch();
        string accepted = string.Join(',', batch.Answers.Where(a => !rejected.Contains(a.ClientAnswerId)).Select(a => $"\"{a.ClientAnswerId}\""));
        string refused = string.Join(',', rejected.Select(id => $"{{\"clientAnswerId\":\"{id}\",\"reason\":\"answered-too-old\"}}"));
        return Json(HttpStatusCode.OK, $"{{\"accepted\":[{accepted}],\"rejected\":[{refused}]}}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(',', header.Value);
        }

        RecordedRequest recorded = new(request.Method, request.RequestUri!.AbsolutePath + request.RequestUri.Query, headers, body);
        Requests.Add(recorded);
        if (Gate is { } gate)
        {
            await gate(recorded);
        }

        HttpResponseMessage response = Respond(recorded) ?? Default(recorded);
        response.RequestMessage = request;
        if (Date is { } date)
        {
            response.Headers.Date = date;
        }

        return response;
    }

    private HttpResponseMessage Default(RecordedRequest request)
    {
        if (string.Equals(request.Path, AntiforgeryPath, StringComparison.Ordinal))
        {
            _tokens++;
            return Json(HttpStatusCode.OK, $"{{\"requestToken\":\"token-{_tokens}\",\"headerName\":\"X-CSRF-TOKEN\"}}");
        }

        return Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    public AnswerBatch Batch() =>
        System.Text.Json.JsonSerializer.Deserialize(Body!, TrainerJsonContext.Default.AnswerBatch)
        ?? throw new InvalidOperationException("The body was JSON null.");
}
