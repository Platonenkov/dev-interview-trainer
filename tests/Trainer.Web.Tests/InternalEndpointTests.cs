using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Trainer.Application.Content;
using Trainer.Content.Reading;
using Trainer.Content.Schema;
using Trainer.TestSupport;
using Trainer.Web.Internal;

namespace Trainer.Web.Tests;

public sealed class InternalEndpointTests(PostgresFixture postgres)
{
    private const int InternalPort = 8081;
    private const int PublicPort = 8080;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private const string QuestionPath = "/en/dotnet/questions/closure-for-loop";
    private const string QuestionFile = "dotnet/csharp/closure-for-loop.yaml";
    private const string OriginalTitle = "What does the code print when lambdas capture a for loop variable?";
    private const string EditedTitle = "Edited closure title";

    private sealed class ImportGate
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Armed;

        public volatile bool TokenWasCancelledWhenReleased;

        public Task Started => _started.Task;

        public Task Aborted => _aborted.Task;

        public void MarkStarted() => _started.TrySetResult();

        public void MarkAborted() => _aborted.TrySetResult();

        public Task WaitForReleaseAsync() => _release.Task;

        public void Release() => _release.TrySetResult();
    }

    // Holds an armed import in flight before anything is written, so a test can drop the client while the import is
    // running. It watches the request abort token, which is not the token the importer receives.
    private sealed class GatedContentReader(IContentReader inner, ImportGate gate, IHttpContextAccessor accessor) : IContentReader
    {
        public async Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct)
        {
            if (gate.Armed)
            {
                accessor.HttpContext!.RequestAborted.Register(gate.MarkAborted);
                gate.MarkStarted();
                await gate.WaitForReleaseAsync();
                gate.TokenWasCancelledWhenReleased = ct.IsCancellationRequested;
            }

            return await inner.ReadAsync(rootPath, ct);
        }
    }

    // Simulates the host shutting down while the import is reading.
    private sealed class StoppingContentReader(IContentReader inner, IHostApplicationLifetime lifetime, ImportGate gate) : IContentReader
    {
        public Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct)
        {
            if (gate.Armed)
            {
                lifetime.StopApplication();
                ct.ThrowIfCancellationRequested();
            }

            return inner.ReadAsync(rootPath, ct);
        }
    }

    private async Task<TrainerWebFactory> CreateFactoryAsync(TrainerWebFactoryOptions? options = null)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, options ?? new TrainerWebFactoryOptions { InternalPort = InternalPort });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private static HttpRequestMessage Build(HttpMethod method, string path, int port, string? token = TrainerWebFactory.ImportToken)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(LocalPortFromHeaderStartupFilter.Header, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<(HttpStatusCode Status, T? Body, HttpResponseMessage Response)> SendJsonAsync<T>(
        HttpClient client, HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        using (request)
        {
            HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            T? body = text.Length == 0 ? default : JsonSerializer.Deserialize(text, info);
            return (response.StatusCode, body, response);
        }
    }

    private static async Task<InternalVersionDto> GetVersionAsync(HttpClient client, int port)
    {
        (HttpStatusCode status, InternalVersionDto? body, HttpResponseMessage response) = await SendJsonAsync(
            client, Build(HttpMethod.Get, "/internal/version", port, token: null), InternalJsonContext.Default.InternalVersionDto);
        using HttpResponseMessage _ = response;
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    private static async Task<InternalImportDto> ImportOkAsync(HttpClient client)
    {
        (HttpStatusCode status, InternalImportDto? body, HttpResponseMessage response) = await SendJsonAsync(
            client, Build(HttpMethod.Post, "/internal/content/import", InternalPort), InternalJsonContext.Default.InternalImportDto);
        using HttpResponseMessage _ = response;
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    private static async Task<string> GetPageAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountImportsAsync(TrainerWebFactory factory)
    {
        await using NpgsqlConnection connection = new(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new("SELECT count(*) FROM content_imports", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Version_OnInternalPort_ReturnsAppVersionAndSchemaRange()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        InternalVersionDto version = await GetVersionAsync(client, InternalPort);

        Assert.Equal((1, 1), (version.ContentSchemaMin, version.ContentSchemaMax));
        Assert.Equal(ContentSchema.RenderVersion, version.RenderVersion);
        Assert.False(string.IsNullOrEmpty(version.AppVersion));
        Assert.False(string.IsNullOrEmpty(version.ActiveContentHash));
    }

    [Fact]
    public async Task Version_OnPublicPort_Returns404WithEmptyBody()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpRequestMessage request = Build(HttpMethod.Get, "/internal/version", PublicPort, token: null);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(body);
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task Version_ReportsActiveContentHashAfterImport()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);

        InternalImportDto imported = await ImportOkAsync(client);
        InternalVersionDto version = await GetVersionAsync(client, InternalPort);

        Assert.Equal("imported", imported.Status);
        Assert.Equal(imported.Hash, version.ActiveContentHash);
    }

    [Fact]
    public async Task Import_SpoofedHostHeaderOnPublicPort_Returns404()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpRequestMessage request = Build(HttpMethod.Post, "/internal/content/import", PublicPort);
        request.Headers.Host = "localhost:" + InternalPort;
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_WithoutToken_Returns401AndImportsNothing()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);
        long before = await CountImportsAsync(factory);

        using HttpRequestMessage request = Build(HttpMethod.Post, "/internal/content/import", InternalPort, token: null);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before, await CountImportsAsync(factory));
    }

    [Theory]
    [InlineData("WRONG-import-token-0123456789abcdef-0123")]
    [InlineData("short")]
    public async Task Import_WrongToken_Returns401(string token)
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpRequestMessage request = Build(HttpMethod.Post, "/internal/content/import", InternalPort, token);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_WrongTokenOfSameLength_Returns401()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        string wrong = new string('X', TrainerWebFactory.ImportToken.Length);

        using HttpRequestMessage request = Build(HttpMethod.Post, "/internal/content/import", InternalPort, wrong);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_ValidToken_ImportsAndEvictsOutputCache()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        await GetPageAsync(client, QuestionPath);
        string cached = await GetPageAsync(client, QuestionPath);
        Assert.Contains(OriginalTitle, cached, StringComparison.Ordinal);
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);

        InternalImportDto imported = await ImportOkAsync(client);
        string after = await GetPageAsync(client, QuestionPath);

        Assert.Equal("imported", imported.Status);
        Assert.True(imported.Imported > 0);
        Assert.Contains(EditedTitle, after, StringComparison.Ordinal);
        Assert.DoesNotContain(OriginalTitle, after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_SameContentTwice_SecondIsUnchanged()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);

        InternalImportDto first = await ImportOkAsync(client);
        InternalImportDto second = await ImportOkAsync(client);

        Assert.Equal("imported", first.Status);
        Assert.Equal("unchanged", second.Status);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public async Task Import_RejectedContent_Returns422WithIssuesAndKeepsPreviousContent()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        string previousHash = (await GetVersionAsync(client, InternalPort)).ActiveContentHash!;
        factory.Content.Write("dotnet/csharp/broken.yaml", "id: not-a-question\n");

        (HttpStatusCode status, InternalImportDto? body, HttpResponseMessage response) = await SendJsonAsync(
            client, Build(HttpMethod.Post, "/internal/content/import", InternalPort), InternalJsonContext.Default.InternalImportDto);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("rejected", body!.Status);
        Assert.NotEmpty(body.Issues);
        Assert.All(body.Issues, issue => Assert.False(string.IsNullOrEmpty(issue.Code)));
        Assert.Equal(previousHash, (await GetVersionAsync(client, InternalPort)).ActiveContentHash);
        Assert.Contains(OriginalTitle, await GetPageAsync(client, QuestionPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_MissingRoot_Returns422ContentRootMissing()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        Directory.Delete(factory.Content.Root, recursive: true);

        (HttpStatusCode status, InternalImportDto? body, HttpResponseMessage response) = await SendJsonAsync(
            client, Build(HttpMethod.Post, "/internal/content/import", InternalPort), InternalJsonContext.Default.InternalImportDto);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        InternalImportIssueDto issue = Assert.Single(body!.Issues);
        Assert.Equal(InternalEndpoints.ContentRootMissingCode, issue.Code);
    }

    [Fact]
    public async Task Import_ClientDisconnect_ImportStillCompletes()
    {
        ImportGate gate = new();
        await using TrainerWebFactory factory = await CreateFactoryAsync(new TrainerWebFactoryOptions
        {
            InternalPort = InternalPort,
            ConfigureServices = services =>
            {
                services.AddHttpContextAccessor();
                services.Replace(ServiceDescriptor.Singleton<IContentReader>(provider => new GatedContentReader(
                    ActivatorUtilities.CreateInstance<ContentReader>(provider), gate, provider.GetRequiredService<IHttpContextAccessor>())));
            },
        });
        using HttpClient client = factory.CreateNonRedirectingClient();
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);

        gate.Armed = true;
        using CancellationTokenSource disconnect = new();
        Task<HttpResponseMessage> abandoned = client.SendAsync(
            Build(HttpMethod.Post, "/internal/content/import", InternalPort), disconnect.Token);
        await gate.Started.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        await disconnect.CancelAsync();

        // The server must have seen the abort while the import is still held; only then is the import released.
        await gate.Aborted.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        gate.Release();

        // The in-memory handler completes the client call only when the server does.
        try
        {
            using HttpResponseMessage late = await abandoned.WaitAsync(StepTimeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        InternalImportDto retry = await ImportOkAsync(client).WaitAsync(StepTimeout, TestContext.Current.CancellationToken);

        Assert.False(gate.TokenWasCancelledWhenReleased);
        Assert.Equal("unchanged", retry.Status);
        Assert.Contains(EditedTitle, await GetPageAsync(client, QuestionPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_HostStopsMidImport_Returns503()
    {
        ImportGate gate = new();
        await using TrainerWebFactory factory = await CreateFactoryAsync(new TrainerWebFactoryOptions
        {
            InternalPort = InternalPort,
            ConfigureServices = services => services.Replace(ServiceDescriptor.Singleton<IContentReader>(provider =>
                new StoppingContentReader(
                    ActivatorUtilities.CreateInstance<ContentReader>(provider), provider.GetRequiredService<IHostApplicationLifetime>(), gate))),
        });
        using HttpClient client = factory.CreateNonRedirectingClient();

        gate.Armed = true;
        using HttpRequestMessage request = Build(HttpMethod.Post, "/internal/content/import", InternalPort);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/internal/foo", PublicPort)]
    [InlineData("POST", "/internal/foo", PublicPort)]
    [InlineData("GET", "/internal/content/import", PublicPort)]
    [InlineData("GET", "/internal/content/import", InternalPort)]
    [InlineData("GET", "/internal/foo", InternalPort)]
    [InlineData("DELETE", "/internal/version", InternalPort)]
    [InlineData("GET", "/internal", PublicPort)]
    public async Task InternalPrefix_UnmappedPathOrMethod_Returns404WithEmptyBody(string method, string path, int port)
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpRequestMessage request = Build(new HttpMethod(method), path, port);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task InternalPrefix_WithoutInternalPort_StillReturnsEmpty404()
    {
        await using TrainerWebFactory factory = await CreateFactoryAsync(new TrainerWebFactoryOptions());
        using HttpClient client = factory.CreateNonRedirectingClient();

        using HttpRequestMessage request = Build(HttpMethod.Get, "/internal/version", PublicPort, token: null);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_TokenNeverLogged()
    {
        CapturingLoggerProvider logs = new();
        await using TrainerWebFactory factory = await CreateFactoryAsync(new TrainerWebFactoryOptions { InternalPort = InternalPort, Logs = logs });
        using HttpClient client = factory.CreateNonRedirectingClient();
        string wrong = new string('X', TrainerWebFactory.ImportToken.Length);
        factory.Content.Edit(QuestionFile, OriginalTitle, EditedTitle);

        using (HttpRequestMessage rejected = Build(HttpMethod.Post, "/internal/content/import", InternalPort, wrong))
        using (HttpResponseMessage response = await client.SendAsync(rejected, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await ImportOkAsync(client);

        Assert.NotEmpty(logs.Entries);
        foreach (CapturedLogEntry entry in logs.Entries)
        {
            string everything = string.Join('\n', [
                entry.Message,
                entry.Exception ?? string.Empty,
                .. entry.State.Values.Select(v => v?.ToString() ?? string.Empty),
                .. entry.Scopes.Select(s => s.Text),
                .. entry.Scopes.SelectMany(s => s.Values.Values).Select(v => v?.ToString() ?? string.Empty),
            ]);
            Assert.DoesNotContain(TrainerWebFactory.ImportToken, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(wrong, everything, StringComparison.Ordinal);
        }
    }
}
