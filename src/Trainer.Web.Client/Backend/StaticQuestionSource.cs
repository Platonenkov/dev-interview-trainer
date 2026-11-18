using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// A session's questions loaded from the content shards as the session reaches them. The loading, sharing and retry rules
/// are <see cref="StaticContentClient"/>'s; this only maps an id to its index entry.
/// </summary>
public sealed class StaticQuestionSource : IQuestionSource
{
    private readonly StaticContentClient _content;
    private readonly string _culture;
    private readonly Dictionary<string, StaticIndexEntryDto> _entries = new(StringComparer.Ordinal);

    public StaticQuestionSource(StaticContentClient content, StaticIndexDto index)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(index);
        _content = content;
        _culture = index.Culture;
        foreach (StaticIndexEntryDto entry in index.Questions)
        {
            _entries.TryAdd(entry.Id, entry);
        }
    }

    public ValueTask<ApiResult<QuestionDto>> GetAsync(string questionId, CancellationToken ct) =>
        _entries.TryGetValue(questionId, out StaticIndexEntryDto? entry)
            ? _content.GetQuestionAsync(_culture, entry, ct)
            : ValueTask.FromResult(new ApiResult<QuestionDto>(null, 404, ApiErrorCodes.QuestionNotFound));

    public void Prefetch(string questionId)
    {
        if (_entries.TryGetValue(questionId, out StaticIndexEntryDto? entry))
        {
            _content.Prefetch(_culture, entry);
        }
    }
}
