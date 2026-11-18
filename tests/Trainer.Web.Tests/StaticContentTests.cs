using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.TestSupport;
using Trainer.Web.Islands;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>The content shards of the static edition (ADR 0023): names, bytes, endpoints and the bootstrap that points at them.</summary>
public sealed partial class StaticContentTests(StaticSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticSiteFixture>
{
    private const string OriginalTitle = "What does the code print when lambdas capture a for loop variable?";
    private const string EditedTitle = "Edited closure title";

    [GeneratedRegex("<!--Blazor:(?<marker>\\{.*?\\})-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ComponentMarker();

    private static readonly StackKey Dotnet = new("dotnet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TrainerWebFactory> CreateStaticFactoryAsync(PostgresFixture postgres) => CreateFactoryAsync(postgres, edition: "Static");

    private static async Task<TrainerWebFactory> CreateFactoryAsync(PostgresFixture postgres, string edition)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = edition,
                    ["App:PublicBaseUrl"] = StaticSiteFixture.Origin + StaticSiteFixture.PathBase + "/",
                },
            });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private static async Task<StaticContentFiles> FilesAsync(TrainerWebFactory factory, string culture)
    {
        StaticContentCatalog catalog = factory.Services.GetRequiredService<StaticContentCatalog>();
        return await catalog.GetAsync(new CultureKey(culture), Dotnet, Ct);
    }

    private static string Sha256Prefix(ReadOnlyMemory<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant()[..16];

    private static string HashSegment(string path)
    {
        // Both shapes end in ".{hash}.json".
        string withoutExtension = path[..^".json".Length];
        return withoutExtension[(withoutExtension.LastIndexOf('.') + 1)..];
    }

    private static string Url(string sitePath) => StaticSiteFixture.PathBase + "/" + sitePath;

    private static StaticIndexDto ParseIndex(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize(bytes.Span, TrainerJsonContext.Default.StaticIndexDto)!;

    private static async Task ImportAsync(TrainerWebFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        ContentImportResult result = await import.ImportAsync(factory.Content.Root, Ct);
        Assert.Equal(ContentImportStatus.Imported, result.Status);
    }

    private static async Task<string> BootstrapIndexPathAsync(TrainerWebFactory factory, string culture)
    {
        StaticSiteDto? staticSite = await BootstrapAsync(factory, culture);
        return Assert.IsType<StaticSiteDto>(staticSite).IndexPath;
    }

    private static async Task<StaticSiteDto?> BootstrapAsync(TrainerWebFactory factory, string culture)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IslandBootstrapFactory bootstrap = scope.ServiceProvider.GetRequiredService<IslandBootstrapFactory>();
        string json = await bootstrap.CreateJsonAsync(
            new CultureKey(culture), Dotnet, new PracticeFiltersDto([], [], [], null, null), new ClaimsPrincipal(new ClaimsIdentity()), Ct);
        return JsonSerializer.Deserialize(json, TrainerJsonContext.Default.IslandBootstrapDto)!.StaticSite;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task Index_ListsEveryLiveQuestionSortedByIdWithItsFileHash(string culture)
    {
        StaticContentFiles files = await FilesAsync(site.Factory, culture);
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IReadOnlyList<QuestionDto> live = await scope.ServiceProvider.GetRequiredService<IStaticSiteQueries>()
            .GetLiveQuestionsAsync(new CultureKey(culture), Dotnet, Ct);

        StaticIndexDto index = ParseIndex(files.Index);

        Assert.True(live.Count >= 3);
        Assert.Equal(StaticIndexDto.CurrentFormat, index.Format);
        Assert.Equal(culture, index.Culture);
        Assert.Equal("dotnet", index.Stack);
        Assert.Equal(
            await scope.ServiceProvider.GetRequiredService<IContentStatusQueries>().GetActiveHashAsync(Ct),
            index.ContentHash);
        Assert.NotEmpty(index.Topics);
        Assert.Equal(live.Select(q => q.Id), index.Questions.Select(q => q.Id));
        Assert.Equal(index.Questions.Select(q => q.Id).Order(StringComparer.Ordinal), index.Questions.Select(q => q.Id));
        Assert.Equal(index.Questions.Count, files.Files.Count);
        foreach ((StaticIndexEntryDto entry, QuestionDto question) in index.Questions.Zip(live))
        {
            Assert.Equal((question.Slug, question.Topic, question.Level, question.Type, question.Title), (entry.Slug, entry.Topic, entry.Level, entry.Type, entry.Title));
            ReadOnlyMemory<byte> file = files.Files[StaticContentPaths.Question(culture, entry.Id, entry.Hash)];
            Assert.Equal(StaticContentHash.Of(file.Span), entry.Hash);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task Index_ListsTopicsOrdinallyByKeyWhateverTheUiOrder(string culture)
    {
        StaticContentFiles files = await FilesAsync(site.Factory, culture);
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IReadOnlyList<Trainer.Application.Stacks.TopicTitle> ui = await scope.ServiceProvider
            .GetRequiredService<Trainer.Application.Stacks.IStackQueries>().GetTopicTitlesAsync(new CultureKey(culture), Dotnet, Ct);

        string[] keys = [.. ParseIndex(files.Index).Topics.Select(t => t.Key)];

        Assert.Equal(ui.Select(t => t.Key.Value).Order(StringComparer.Ordinal), keys);
        Assert.Equal(["async", "concurrency", "csharp", "generics"], keys);
        Assert.Equal(ui.Select(t => t.Title).Order(StringComparer.Ordinal), ParseIndex(files.Index).Topics.Select(t => t.Title).Order(StringComparer.Ordinal));
        // The English UI order is by title and differs from key order, so the shard order is a deliberate difference.
        if (culture == "en")
        {
            Assert.NotEqual(ui.Select(t => t.Key.Value), keys);
        }
    }

    [Fact]
    public async Task DataEndpoints_AnswerHeadWithTheGetHeadersAndNoBody()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "en");
        string questionPath = files.Files.Keys.First();
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        foreach ((string path, int length) in new[] { (files.IndexPath, files.Index.Length), (questionPath, files.Files[questionPath].Length) })
        {
            using HttpRequestMessage request = new(HttpMethod.Head, Url(path));
            using HttpResponseMessage response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal(length, response.Content.Headers.ContentLength);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        using HttpRequestMessage miss = new(HttpMethod.Head, Url("data/en/dotnet/index.0000000000000000.json"));
        using HttpResponseMessage missing = await client.SendAsync(miss, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, missing.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task QuestionFiles_AreTheSessionDtoJson()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "ru");
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IReadOnlyList<QuestionDto> live = await scope.ServiceProvider.GetRequiredService<IStaticSiteQueries>()
            .GetLiveQuestionsAsync(new CultureKey("ru"), Dotnet, Ct);

        foreach (StaticIndexEntryDto entry in ParseIndex(files.Index).Questions)
        {
            QuestionDto expected = live.Single(q => q.Id == entry.Id);
            ReadOnlyMemory<byte> file = files.Files[StaticContentPaths.Question("ru", entry.Id, entry.Hash)];

            // Compared as parsed JSON: the file keeps Cyrillic literal where the default serialiser would escape it.
            Assert.True(
                JsonNode.DeepEquals(
                    JsonNode.Parse(JsonSerializer.Serialize(expected, TrainerJsonContext.Default.QuestionDto)),
                    JsonNode.Parse(file.Span)),
                $"Question file of {entry.Id} differs from the session DTO.");
        }
    }

    [Fact]
    public async Task QuestionFiles_KeepCyrillicReadableAndMarkupEscaped()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "ru");

        string text = string.Concat(files.Files.Values.Select(v => System.Text.Encoding.UTF8.GetString(v.Span)));

        Assert.Contains("Зачем нужен ConfigureAwait", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
        Assert.Contains("\\u003Cp\\u003E", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task FileNames_CarryTheSha256PrefixOfTheirBytes(string culture)
    {
        StaticContentFiles files = await FilesAsync(site.Factory, culture);

        Assert.NotEmpty(files.Files);
        Assert.Matches($"^data/{culture}/dotnet/index\\.[0-9a-f]{{16}}\\.json$", files.IndexPath);
        Assert.Equal(Sha256Prefix(files.Index), HashSegment(files.IndexPath));
        foreach ((string path, ReadOnlyMemory<byte> bytes) in files.Files)
        {
            Assert.Matches($"^data/{culture}/q/[0-9A-Za-z]+\\.[0-9a-f]{{16}}\\.json$", path);
            Assert.Equal(Sha256Prefix(bytes), HashSegment(path));
        }
    }

    [Fact]
    public async Task SameContent_YieldsTheSameNames()
    {
        await using TrainerWebFactory other = await CreateStaticFactoryAsync(postgres);

        foreach (string culture in new[] { "en", "ru" })
        {
            StaticContentFiles first = await FilesAsync(site.Factory, culture);
            StaticContentFiles second = await FilesAsync(other, culture);

            Assert.Equal(first.IndexPath, second.IndexPath);
            Assert.Equal(first.Files.Keys.Order(StringComparer.Ordinal), second.Files.Keys.Order(StringComparer.Ordinal));
            Assert.True(first.Index.Span.SequenceEqual(second.Index.Span));
        }
    }

    [Fact]
    public async Task ChangedQuestion_ChangesOnlyItsFileAndTheIndex()
    {
        await using TrainerWebFactory factory = await CreateStaticFactoryAsync(postgres);
        StaticContentFiles before = await FilesAsync(factory, "en");
        StaticContentFiles russianBefore = await FilesAsync(factory, "ru");

        factory.Content.Edit("dotnet/csharp/closure-for-loop.yaml", OriginalTitle, EditedTitle);
        await ImportAsync(factory);
        StaticContentFiles after = await FilesAsync(factory, "en");
        StaticContentFiles russianAfter = await FilesAsync(factory, "ru");

        Assert.NotEqual(before.IndexPath, after.IndexPath);
        string[] removed = [.. before.Files.Keys.Except(after.Files.Keys, StringComparer.Ordinal)];
        string[] added = [.. after.Files.Keys.Except(before.Files.Keys, StringComparer.Ordinal)];
        string changedId = ParseIndex(after.Index).Questions.Single(q => q.Title == EditedTitle).Id;
        Assert.Equal([StaticContentPaths.Question("en", changedId, ParseIndex(before.Index).Questions.Single(q => q.Id == changedId).Hash)], removed);
        Assert.Equal([StaticContentPaths.Question("en", changedId, ParseIndex(after.Index).Questions.Single(q => q.Id == changedId).Hash)], added);
        Assert.Equal(before.Files.Count, after.Files.Count);
        foreach (string unchanged in before.Files.Keys.Except(removed, StringComparer.Ordinal))
        {
            Assert.True(before.Files[unchanged].Span.SequenceEqual(after.Files[unchanged].Span));
        }

        // The English title is not part of any Russian file.
        Assert.Equal(russianBefore.Files.Keys.Order(StringComparer.Ordinal), russianAfter.Files.Keys.Order(StringComparer.Ordinal));

        // The old names are gone from the endpoints at once: a page of the old build would see a 404.
        using HttpClient client = factory.CreateNonRedirectingClient();
        using HttpResponseMessage oldIndex = await client.GetAsync(Url(before.IndexPath), Ct);
        using HttpResponseMessage newIndex = await client.GetAsync(Url(after.IndexPath), Ct);
        using HttpResponseMessage oldQuestion = await client.GetAsync(Url(removed[0]), Ct);
        using HttpResponseMessage newQuestion = await client.GetAsync(Url(added[0]), Ct);
        Assert.Equal(HttpStatusCode.NotFound, oldIndex.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newIndex.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldQuestion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newQuestion.StatusCode);
    }

    [Fact]
    public async Task DataEndpoints_ServeTheExactBytes()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "en");
        string questionPath = files.Files.Keys.Order(StringComparer.Ordinal).First();
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage index = await client.GetAsync(Url(files.IndexPath), Ct);
        using HttpResponseMessage question = await client.GetAsync(Url(questionPath), Ct);

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("application/json; charset=utf-8", index.Content.Headers.ContentType?.ToString());
        byte[] indexBody = await index.Content.ReadAsByteArrayAsync(Ct);
        byte[] questionBody = await question.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(files.Index.Span.SequenceEqual(indexBody));
        Assert.Equal(HttpStatusCode.OK, question.StatusCode);
        Assert.Equal("application/json; charset=utf-8", question.Content.Headers.ContentType?.ToString());
        Assert.True(files.Files[questionPath].Span.SequenceEqual(questionBody));
    }

    [Fact]
    public async Task WrongHash_Returns404WithEmptyBody()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "en");
        string questionPath = files.Files.Keys.Order(StringComparer.Ordinal).First();
        string questionId = questionPath["data/en/q/".Length..questionPath.IndexOf('.', "data/en/q/".Length)];
        string[] misses =
        [
            files.IndexPath.Replace(HashSegment(files.IndexPath), "0000000000000000", StringComparison.Ordinal),
            questionPath.Replace(HashSegment(questionPath), "0000000000000000", StringComparison.Ordinal),
            $"data/en/q/{questionId}.json",
            "data/en/q/no-such-question.0000000000000000.json",
            "data/en/dotnet/index.json",
            "data/en/dotnet/index.NOTHEX0000000000.json",
            "data/en/no-such-stack/index.0000000000000000.json",
            files.IndexPath.Replace("data/en/", "data/de/", StringComparison.Ordinal),
            "data/en/dotnet/other.0000000000000000.json",
            "data/",
            "data/en",
        ];
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        foreach (string miss in misses)
        {
            using HttpResponseMessage response = await client.GetAsync(Url(miss), Ct);

            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{miss} answered {(int)response.StatusCode}.");
            Assert.Equal(0, response.Content.Headers.ContentLength);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        }
    }

    [Fact]
    public async Task DataEndpoints_AreNotMappedInServerEdition()
    {
        string validPath = (await FilesAsync(site.Factory, "en")).IndexPath;
        string questionPath = (await FilesAsync(site.Factory, "en")).Files.Keys.First();
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();
        using HttpClient client = server.CreateNonRedirectingClient();

        using HttpResponseMessage index = await client.GetAsync("/" + validPath, Ct);
        using HttpResponseMessage question = await client.GetAsync("/" + questionPath, Ct);

        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, question.StatusCode);
        Assert.DoesNotContain("json", index.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(server.Services.GetService<StaticContentCatalog>());
    }

    [Fact]
    public async Task IslandBootstrap_NamesTheIndexPathInStaticEditionOnly()
    {
        foreach (string culture in new[] { "en", "ru" })
        {
            StaticSiteDto? staticSite = await BootstrapAsync(site.Factory, culture);
            StaticContentFiles files = await FilesAsync(site.Factory, culture);

            Assert.NotNull(staticSite);
            Assert.Equal(files.IndexPath, staticSite.IndexPath);
            Assert.False(staticSite.IndexPath.StartsWith('/'));
            Assert.Equal("https://github.com/Platonenkov/dev-interview-trainer/issues/new", staticSite.IssuesNewUrl);
        }

        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();
        Assert.Null(await BootstrapAsync(server, "en"));
    }

    [Fact]
    public async Task IslandBootstrap_UsesTheConfiguredRepository()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = StaticSiteFixture.Origin + StaticSiteFixture.PathBase + "/",
                    ["App:StaticSite:RepositoryUrl"] = "https://github.com/example/fork/",
                },
            });
        await factory.WaitUntilReadyAsync();

        StaticSiteDto? staticSite = await BootstrapAsync(factory, "en");

        Assert.Equal("https://github.com/example/fork/issues/new", staticSite?.IssuesNewUrl);
        Assert.Equal((await FilesAsync(factory, "en")).IndexPath, await BootstrapIndexPathAsync(factory, "en"));
    }

    [Fact]
    public async Task IslandPage_StateNamesTheIndexPath()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "en");
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage page = await client.GetAsync(Url("en/dotnet/practice/"), Ct);
        string body = await page.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Match match = ComponentMarker().Match(body);
        Assert.True(match.Success, "No WebAssembly component marker.");
        using JsonDocument marker = JsonDocument.Parse(match.Groups["marker"].Value);
        string values = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(marker.RootElement.GetProperty("parameterValues").GetString()!));
        using JsonDocument parameters = JsonDocument.Parse(values);
        IslandBootstrapDto bootstrap = JsonSerializer.Deserialize(parameters.RootElement[0].GetString()!, TrainerJsonContext.Default.IslandBootstrapDto)!;
        Assert.Equal(files.IndexPath, bootstrap.StaticSite?.IndexPath);
    }

    [Fact]
    public async Task DataEndpoints_SetNoCookie()
    {
        StaticContentFiles files = await FilesAsync(site.Factory, "ru");
        string questionPath = files.Files.Keys.First();
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        foreach (string path in new[] { files.IndexPath, questionPath, "data/ru/dotnet/index.0000000000000000.json" })
        {
            using HttpResponseMessage response = await client.GetAsync(Url(path), Ct);

            Assert.False(response.Headers.Contains("Set-Cookie"), $"{path} set a cookie.");
        }
    }

    [Theory]
    [InlineData("https://github.com/Platonenkov/dev-interview-trainer", "https://github.com/Platonenkov/dev-interview-trainer/issues/new")]
    [InlineData("https://github.com/o/r/", "https://github.com/o/r/issues/new")]
    public void StaticSiteOptions_DeriveTheNewIssueUrl(string repository, string expected)
    {
        StaticSiteOptions options = new() { RepositoryUrl = repository };

        options.EnsureValid();

        Assert.Equal(expected, options.IssuesNewUrl);
        Assert.Equal("https://github.com/Platonenkov/dev-interview-trainer", new StaticSiteOptions().RepositoryUrl);
    }

    [Theory]
    [InlineData("http://github.com/o/r")]
    [InlineData("github.com/o/r")]
    [InlineData("https://github.com/o/r?x=1")]
    [InlineData("https://github.com/o/r#frag")]
    [InlineData("https://user:pass@github.com/o/r")]
    [InlineData("")]
    public void StaticSiteOptions_RefuseARepositoryThatIsNotPlainHttps(string repository)
    {
        StaticSiteOptions options = new() { RepositoryUrl = repository };

        Assert.Throws<InvalidOperationException>(options.EnsureValid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdef0", false)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcdeg", false)]
    [InlineData("0123456789abcdef", true)]
    public void StaticContentHash_IsValidOnlyForSixteenLowercaseHexCharacters(string value, bool expected)
    {
        Assert.Equal(expected, StaticContentHash.IsValid(value));
        Assert.Equal(16, StaticContentHash.Of("x"u8).Length);
        Assert.Equal("2d711642b726b044", StaticContentHash.Of("x"u8));
    }
}
