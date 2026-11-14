using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Trainer.Contracts;
using Trainer.Contracts.Storage;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// The active mock interview of one owner, so a reload resumes it: the questions once in their own key, and a small
/// progress document saved with a revision check, so a second tab writing the same mock is reported instead of
/// silently overwriting the first.
/// </summary>
public sealed class ActiveMockStore(IBrowserStore store)
{
    private readonly NewerVersionKeys _newer = new();

    /// <summary>Raised when <see cref="HasNewerVersion"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The active mock was saved by a newer app version; it is left untouched until the page is reloaded.</summary>
    public bool HasNewerVersion => _newer.Any;

    /// <summary>Writes the questions of a new mock; called once, when the mock starts.</summary>
    public async ValueTask<SaveResult> SaveQuestionsAsync(string owner, string sessionId, IReadOnlyList<QuestionDto> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        JsonElement document = JsonSerializer.SerializeToElement(
            new StoredMockQuestions(StoredMockQuestions.CurrentVersion, sessionId, questions),
            TrainerJsonContext.Default.StoredMockQuestions);
        StoreResult result = await MutateAsync(StoreOp.Put(StorageKeys.MockQuestions(owner, sessionId), document));
        return result.Ok ? SaveResult.Saved : SaveResult.Failed;
    }

    /// <summary>
    /// The saved progress and its questions, or <c>null</c> for each that is missing, unreadable or written by a newer
    /// app. A newer document is left untouched.
    /// </summary>
    public async ValueTask<(MockProgress? Progress, IReadOnlyList<QuestionDto>? Questions)> LoadAsync(string owner)
    {
        StoredDocument progressDocument = await ReadAsync(StorageKeys.MockProgress(owner));
        MockProgress? progress = Read(progressDocument, TrainerJsonContext.Default.MockProgress);
        if (progress?.SessionId is null || progress.Attempts is null)
        {
            return (null, null);
        }

        StoredDocument questionsDocument = await ReadAsync(StorageKeys.MockQuestions(owner, progress.SessionId));
        StoredMockQuestions? questions = Read(questionsDocument, TrainerJsonContext.Default.StoredMockQuestions);
        bool matches = questions?.Questions is not null && string.Equals(questions.SessionId, progress.SessionId, StringComparison.Ordinal);
        return (progress, matches ? questions!.Questions : null);
    }

    /// <summary>
    /// Saves <paramref name="progress"/> over revision <c>progress.Revision - 1</c> (0 means no saved progress) of the same
    /// mock session. Any other stored revision, or a stored document of another session (a stale tab must not overwrite a
    /// mock that happens to sit at the revision it expects), means another tab saved first:
    /// <see cref="SaveResult.OtherWriter"/>, and nothing is written.
    /// </summary>
    public async ValueTask<SaveResult> SaveProgressAsync(string owner, MockProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        JsonElement document = JsonSerializer.SerializeToElement(progress, TrainerJsonContext.Default.MockProgress);
        string key = StorageKeys.MockProgress(owner);
        StoreResult result = await store.MutateAsync(StoreOp.PutIfRevision(key, document, progress.Revision - 1, progress.SessionId));
        if (string.Equals(result.Error, StoreErrors.Corrupt, StringComparison.Ordinal)
            && (await store.MutateAsync(StoreOp.Quarantine(key))).Ok)
        {
            // An unreadable value names no revision and no session, so it cannot be another tab's newer save: after it
            // is set aside this tab's progress is the best there is. Expecting revision 0 still refuses if another tab
            // wrote a readable document in between.
            result = await store.MutateAsync(StoreOp.PutIfRevision(key, document, 0, progress.SessionId));
        }

        RaiseIf(_newer.Note(key, result));
        if (result.Ok)
        {
            return SaveResult.Saved;
        }

        return string.Equals(result.Error, StoreErrors.Revision, StringComparison.Ordinal) ? SaveResult.OtherWriter : SaveResult.Failed;
    }

    /// <summary>
    /// Deletes the progress and then the questions of <paramref name="sessionId"/>; another session's progress stays. The
    /// questions go only after the progress delete succeeded: progress whose questions are gone could not be resumed.
    /// </summary>
    /// <returns>True when both deletes succeeded (a key that is already gone counts as deleted).</returns>
    public async ValueTask<bool> ClearAsync(string owner, string sessionId)
    {
        if (!(await MutateAsync(StoreOp.Delete(StorageKeys.MockProgress(owner), sessionId))).Ok)
        {
            return false;
        }

        return (await MutateAsync(StoreOp.Delete(StorageKeys.MockQuestions(owner, sessionId), sessionId))).Ok;
    }

    private async ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        StoreResult result = await store.MutateWithQuarantineAsync(op);
        RaiseIf(_newer.Note(op.Key, result));
        return result;
    }

    private async ValueTask<StoredDocument> ReadAsync(string key)
    {
        StoredDocument document = await store.ReadDocumentAsync(key);
        RaiseIf(_newer.Note(key, document.Kind));
        return document;
    }

    private void RaiseIf(bool changed)
    {
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static T? Read<T>(StoredDocument document, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (document.Kind != StoredKind.Valid)
        {
            return null;
        }

        try
        {
            return document.Root.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
