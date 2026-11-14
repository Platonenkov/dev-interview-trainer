using System.Security.Claims;
using System.Text.Json;
using Trainer.Application.Progress;
using Trainer.Application.Stacks;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Web.Auth;

namespace Trainer.Web.Islands;

/// <summary>
/// Builds the one JSON string an island page hands its island: everything the island needs before it is usable, so it
/// makes no request first. A string parameter keeps the island's parameters primitive and trimming-safe.
/// </summary>
public sealed class IslandBootstrapFactory(IStackQueries stacks, IProgressQueries progress)
{
    private static readonly string[] Levels = [.. LevelKeys.All.Select(LevelKeys.ToKey)];
    private static readonly string[] Types = [.. QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey)];

    /// <summary>An <see cref="IslandBootstrapDto"/> serialised with <see cref="TrainerJsonContext"/>; <c>Me</c> is null for anonymous users.</summary>
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
            me);
        return JsonSerializer.Serialize(bootstrap, TrainerJsonContext.Default.IslandBootstrapDto);
    }
}
