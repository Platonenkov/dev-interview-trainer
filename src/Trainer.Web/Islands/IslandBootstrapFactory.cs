using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trainer.Application.Progress;
using Trainer.Application.Stacks;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Web.Auth;
using Trainer.Web.StaticSite;
using Trainer.Web.Startup;

namespace Trainer.Web.Islands;

/// <summary>
/// Builds the one JSON string an island page hands its island: everything the island needs before it is usable, so it
/// makes no request first. A string parameter keeps the island's parameters primitive and trimming-safe.
/// </summary>
/// <remarks>
/// <paramref name="staticContent"/> is registered in the static edition only, where the bootstrap also names the content
/// index so the island fetches its questions from the shards instead of an API.
/// </remarks>
public sealed class IslandBootstrapFactory(
    IStackQueries stacks,
    IProgressQueries progress,
    IOptions<AppOptions> app,
    StaticContentCatalog? staticContent = null)
{
    private static readonly string[] Levels = [.. LevelKeys.All.Select(LevelKeys.ToKey)];
    private static readonly string[] Types = [.. QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey)];

    /// <summary>
    /// An <see cref="IslandBootstrapDto"/> serialised with <see cref="TrainerJsonContext"/>; <c>Me</c> is null for anonymous
    /// users and <c>StaticSite</c> is null outside the static edition.
    /// </summary>
    public async Task<string> CreateJsonAsync(
        CultureKey culture, StackKey stack, PracticeFiltersDto filters, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(user);

        IReadOnlyList<TopicTitle> topics = await stacks.GetTopicTitlesAsync(culture, stack, ct);
        MeDto? me = user.GetUserId() is { } id ? await progress.GetMeAsync(id, culture, stack, ct) : null;
        IslandBootstrapDto bootstrap = new(
            culture.Value,
            stack.Value,
            [.. topics.Select(t => new TopicDto(t.Key.Value, t.Title))],
            Levels,
            Types,
            filters,
            me,
            await CreateStaticSiteAsync(culture, stack, ct));
        return JsonSerializer.Serialize(bootstrap, TrainerJsonContext.Default.IslandBootstrapDto);
    }

    private async Task<StaticSiteDto?> CreateStaticSiteAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        if (app.Value.Edition != SiteEdition.Static)
        {
            return null;
        }

        StaticContentCatalog catalog = staticContent
            ?? throw new InvalidOperationException("The static edition needs StaticContentCatalog to be registered.");
        StaticContentFiles files = await catalog.GetAsync(culture, stack, ct);
        return new StaticSiteDto(files.IndexPath, app.Value.StaticSite.IssuesNewUrl);
    }
}
