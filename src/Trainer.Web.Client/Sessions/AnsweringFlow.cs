using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// The answering protocol Practice and Review share, so a fix to it lands in both. Outbox first (spec 8.8): while a
/// graded answer is written, <see cref="IsSaving"/> is true and the island holds its pre-answer render
/// (<c>ShouldRender</c>), so the verdict and Next never show for an answer a reload could still lose. The write is
/// bounded by <see cref="SlowStorage"/>: a Web Lock that is never granted must not freeze the island; past the bound the
/// answer stays in the outbox store's memory, is written by a later write, and <see cref="IsSlow"/> says so.
/// </summary>
public sealed class AnsweringFlow
{
    private readonly SlowStorage _slowStorage;
    private readonly IAnswerGrader _grader;
    private readonly Func<QuestionAttempt, AnswerDto> _toAnswer;
    private readonly Func<AnswerDto, Task> _write;
    private readonly Action? _afterSave;
    private bool _focusNext;

    /// <param name="toAnswer">Builds the outbox entry with the island's mode.</param>
    /// <param name="write">Writes it to the owner's outbox.</param>
    /// <param name="afterSave">Runs after every save, such as a background flush for a signed-in owner.</param>
    public AnsweringFlow(
        SlowStorage slowStorage,
        IAnswerGrader grader,
        Func<QuestionAttempt, AnswerDto> toAnswer,
        Func<AnswerDto, Task> write,
        Action? afterSave)
    {
        ArgumentNullException.ThrowIfNull(slowStorage);
        ArgumentNullException.ThrowIfNull(grader);
        ArgumentNullException.ThrowIfNull(toAnswer);
        ArgumentNullException.ThrowIfNull(write);
        _slowStorage = slowStorage;
        _grader = grader;
        _toAnswer = toAnswer;
        _write = write;
        _afterSave = afterSave;
    }

    /// <summary>Raised when <see cref="IsSlow"/> changes.</summary>
    public event Action? SlowChanged
    {
        add => _slowStorage.Changed += value;
        remove => _slowStorage.Changed -= value;
    }

    /// <summary>A graded answer is being written; the island renders nothing new meanwhile.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Graded answers whose write returned or outlasted its bound (<c>data-answers-saved</c>; tests wait on it).</summary>
    public int Saved { get; private set; }

    /// <summary>A write outlasted its bound and is still running.</summary>
    public bool IsSlow => _slowStorage.IsSlow;

    /// <summary>Whether Next may run for <paramref name="attempt"/>: it is done and nothing is being saved.</summary>
    public bool CanGoNext(QuestionAttempt? attempt) => !IsSaving && attempt?.IsDone == true;

    /// <summary>
    /// True once after an answer or a skip replaced the control that had focus; the island then focuses its Next button.
    /// </summary>
    public bool TakeFocusNext()
    {
        bool focus = _focusNext;
        _focusNext = false;
        return focus;
    }

    /// <summary>Grades and saves an automatic kind; ignored while saving or when the attempt cannot be submitted.</summary>
    public Task SubmitAsync(QuestionAttempt? attempt)
    {
        if (IsSaving || attempt is not { CanSubmit: true })
        {
            return Task.CompletedTask;
        }

        return GradeAndSaveAsync(attempt, () =>
        {
            attempt.SubmitAutomatic();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Records a self-grade and saves it; ignored while saving or when the attempt cannot take one.</summary>
    public Task SelfGradeAsync(QuestionAttempt? attempt, Grade grade, CancellationToken ct)
    {
        if (IsSaving || attempt is not { CanSelfAssess: true })
        {
            return Task.CompletedTask;
        }

        return GradeAndSaveAsync(attempt, async () => await attempt.SubmitSelfAssessmentAsync(grade, _grader, ct));
    }

    /// <summary>Moves on from a revealed self-assessed question without an answer.</summary>
    public void Skip(QuestionAttempt? attempt)
    {
        if (attempt is { IsDone: false, ReferenceRevealed: true, Kind: QuestionKind.SelfAssessed })
        {
            attempt.Skip();
            _focusNext = true;
        }
    }

    /// <summary>
    /// Runs a keyboard shortcut against <paramref name="attempt"/> (<see cref="KeyboardShortcuts.Resolve"/>); returns
    /// whether it did anything, so the island re-renders. Ignored while saving.
    /// </summary>
    public async Task<bool> HandleShortcutAsync(ShortcutKey key, QuestionAttempt? attempt, Action next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (IsSaving)
        {
            return false;
        }

        ShortcutAction action = KeyboardShortcuts.Resolve(key, attempt, CanGoNext(attempt));
        switch (action.Command)
        {
            case ShortcutCommand.PickOption:
                attempt!.Toggle(attempt.Options[action.OptionIndex].Id);
                return true;
            case ShortcutCommand.SelfGrade:
                await SelfGradeAsync(attempt, action.Grade, ct);
                return true;
            case ShortcutCommand.Submit:
                await SubmitAsync(attempt);
                return true;
            case ShortcutCommand.Next:
                next();
                return true;
            default:
                return false;
        }
    }

    private async Task GradeAndSaveAsync(QuestionAttempt attempt, Func<ValueTask> grade)
    {
        try
        {
            IsSaving = true;
            await grade();
            await _slowStorage.WaitAsync(WriteAsync(_toAnswer(attempt)), false);
        }
        finally
        {
            IsSaving = false;
            if (attempt.IsDone)
            {
                Saved++;
                _focusNext = true;
            }
        }

        _afterSave?.Invoke();
    }

    private async Task<bool> WriteAsync(AnswerDto answer)
    {
        await _write(answer);
        return true;
    }
}
