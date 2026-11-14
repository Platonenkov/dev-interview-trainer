using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Grading;
using Trainer.Domain.Questions;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// One question as the user works on it, in any mode. The answer id is fixed when the attempt is created, so however
/// often the attempt is completed or restored it never yields a second answer. Times come from the
/// <see cref="ServerClock"/> at the moment of input (ADR 0005). Eligibility: an automatic kind yields an answer only once
/// graded with a response; a self-assessed kind only from an explicit self-grade, notes or not; a skip yields none.
/// </summary>
public sealed class QuestionAttempt
{
    private readonly ServerClock _clock;
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
    private readonly HashSet<int> _lines = [];

    public QuestionAttempt(QuestionDto question, string clientAnswerId, Random random, ServerClock clock)
        : this(question, clientAnswerId, OptionShuffler.Shuffle(Checked(question).Options, random), clock)
    {
    }

    private QuestionAttempt(QuestionDto question, string clientAnswerId, IReadOnlyList<OptionDto> options, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentException.ThrowIfNullOrEmpty(clientAnswerId);
        ArgumentNullException.ThrowIfNull(clock);

        Question = question;
        ClientAnswerId = clientAnswerId;
        Kind = QuestionKinds.Of(question);
        Options = options;
        _clock = clock;
    }

    public QuestionDto Question { get; }

    public string ClientAnswerId { get; }

    public QuestionKind Kind { get; }

    /// <summary>The options in the order they are shown; the order is part of the attempt's state.</summary>
    public IReadOnlyList<OptionDto> Options { get; }

    public bool UsedHint { get; private set; }

    public IReadOnlySet<string> Picked => _picked;

    /// <summary>1-based line numbers marked on a find-bug question.</summary>
    public IReadOnlySet<int> SelectedLines => _lines;

    public string Typed { get; private set; } = "";

    /// <summary>The user's own answer text for a self-assessed question. Only the user reads it.</summary>
    public string Notes { get; private set; } = "";

    /// <summary>Server time of the last pick, line toggle or non-empty typed change; hints and notes do not count.</summary>
    public DateTimeOffset? LastInputAt { get; private set; }

    /// <summary>True once the response can no longer change: after grading, a skip, or <see cref="Freeze"/>.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>The final grade: the automatic one, or the grader's result for a self-assessed question.</summary>
    public Grade? Grade { get; private set; }

    /// <summary>The grade the user chose for a self-assessed question, before the grader's hint cap.</summary>
    public Grade? SelfGrade { get; private set; }

    /// <summary>Server time of grading; the answer keeps it for good.</summary>
    public DateTimeOffset? AnsweredAt { get; private set; }

    public bool ReferenceRevealed { get; private set; }

    /// <summary>A self-grade is with the grader right now; the UI disables its grade buttons meanwhile.</summary>
    public bool IsAssessing { get; private set; }

    /// <summary>A self-assessed question whose reference is shown and which is neither graded, skipped nor being graded.</summary>
    public bool CanSelfAssess => Kind == QuestionKind.SelfAssessed && ReferenceRevealed && !IsDone && !IsAssessing;

    public bool Skipped { get; private set; }

    /// <summary>Graded or skipped: feedback can be shown and the session can move on.</summary>
    public bool IsDone => Grade is not null || Skipped;

    /// <summary>
    /// Automatic kinds: a pick, a marked line or non-empty typed text (a hint alone is not a response). Self-assessed
    /// kinds: only an explicit self-grade.
    /// </summary>
    public bool HasResponse => Kind switch
    {
        QuestionKind.SelfAssessed => Grade is not null,
        QuestionKind.FindBug => _lines.Count > 0,
        QuestionKind.OutputTyped => HasTypedText(Typed),
        _ => _picked.Count > 0,
    };

    /// <summary>
    /// Whether <see cref="SubmitAutomatic"/> may run now from the UI. Always false for self-assessed kinds: they are
    /// answered through <see cref="SubmitSelfAssessmentAsync"/>.
    /// </summary>
    public bool CanSubmit => Kind != QuestionKind.SelfAssessed && !IsFrozen && HasResponse;

    /// <summary>
    /// Records that the hint was opened, which caps the grade at partial. Ignored when frozen, without a hint, or once
    /// the reference answer is shown (a hint after the answer could only lower the grade).
    /// </summary>
    public void RevealHint()
    {
        if (!IsFrozen && !ReferenceRevealed && Question.HintHtml is not null)
        {
            UsedHint = true;
        }
    }

    /// <summary>Single-answer kinds replace the pick, multi toggles it. Ignored when frozen or for an unknown id.</summary>
    public void Toggle(string optionId)
    {
        if (IsFrozen || !Kind.IsChoice() || !Options.Any(o => string.Equals(o.Id, optionId, StringComparison.Ordinal)))
        {
            return;
        }

        if (Kind == QuestionKind.MultiChoice)
        {
            if (!_picked.Remove(optionId))
            {
                _picked.Add(optionId);
            }
        }
        else
        {
            _picked.Clear();
            _picked.Add(optionId);
        }

        LastInputAt = _clock.Now();
    }

    /// <summary>Marks or unmarks a 1-based line. Ignored when frozen, for other kinds or a line outside the code.</summary>
    public void ToggleLine(int line)
    {
        if (IsFrozen || Kind != QuestionKind.FindBug || line < 1 || line > (Question.CodeLinesHtml?.Count ?? 0))
        {
            return;
        }

        if (!_lines.Remove(line))
        {
            _lines.Add(line);
        }

        LastInputAt = _clock.Now();
    }

    public void SetTyped(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsFrozen || Kind != QuestionKind.OutputTyped || string.Equals(value, Typed, StringComparison.Ordinal))
        {
            return;
        }

        Typed = value;
        if (HasTypedText(value))
        {
            LastInputAt = _clock.Now();
        }
    }

    public void SetNotes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsFrozen)
        {
            Notes = value;
        }
    }

    /// <summary>Stops every later input (Mock: the time ran out or the user finished). Grading still works.</summary>
    public void Freeze() => IsFrozen = true;

    /// <summary>Shows the reference answer, which a self-assessment needs first; Mock calls it when it enters Assessment.</summary>
    public void RevealReference() => ReferenceRevealed = true;

    /// <summary>Grades an automatic kind, stamps <see cref="AnsweredAt"/> with the server time and freezes.</summary>
    /// <exception cref="InvalidOperationException">A self-assessed kind, already graded, or no response yet.</exception>
    public Grade SubmitAutomatic() => SubmitAutomaticCore(null);

    /// <summary>
    /// Mock: grades an automatic kind like <see cref="SubmitAutomatic"/>, but <see cref="AnsweredAt"/> is the time of the
    /// last input, not of grading, because the user answered then and the grade is only computed when the mock ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">A self-assessed kind, already graded, or no response yet.</exception>
    public Grade SubmitAutomaticAtLastInput() => SubmitAutomaticCore(LastInputAt);

    private Grade SubmitAutomaticCore(DateTimeOffset? answeredAt)
    {
        if (Kind == QuestionKind.SelfAssessed)
        {
            throw new InvalidOperationException("A self-assessed question is graded by the user.");
        }

        if (IsDone)
        {
            throw new InvalidOperationException("The attempt is already graded.");
        }

        if (!HasResponse)
        {
            throw new InvalidOperationException("There is no response to grade.");
        }

        Grade grade = Kind switch
        {
            QuestionKind.MultiChoice => AutoGrading.Multi(CorrectOptionIds(), [.. _picked], UsedHint),
            QuestionKind.OutputTyped => AutoGrading.TypedOutput(Question.AcceptedOutputs ?? [], Typed, UsedHint),
            QuestionKind.FindBug => AutoGrading.FindBug(Question.BugLineSets ?? [], [.. _lines], UsedHint),
            _ => GradeSingle(),
        };

        Complete(grade, answeredAt);
        return grade;
    }

    /// <summary>
    /// Records the user's explicit grade through <paramref name="grader"/>; this creates the answer whether or not any
    /// notes were written. <see cref="AnsweredAt"/> is the server time of the choice.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Not a self-assessed kind, the reference answer was not revealed, or the attempt is already graded, skipped or being graded.
    /// </exception>
    public async ValueTask<Grade> SubmitSelfAssessmentAsync(Grade choice, IAnswerGrader grader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grader);
        EnsureAssessable();

        DateTimeOffset chosenAt = _clock.Now();
        IsAssessing = true;
        try
        {
            // Kind already rejected unknown types, so this always succeeds.
            if (!QuestionTypeKeys.TryParse(Question.Type, out QuestionType type))
            {
                throw new InvalidOperationException($"Unknown question type '{Question.Type}'.");
            }

            Grade grade = await grader.GradeAsync(
                new SelfGradedAnswer(new QuestionId(Question.Id), type, Notes, choice, UsedHint),
                ct);
            SelfGrade = choice;
            Complete(grade, chosenAt);
            return grade;
        }
        finally
        {
            IsAssessing = false;
        }
    }

    /// <summary>Moves on from a self-assessed question without an answer (Practice "Skip", Mock assessment "Skip").</summary>
    /// <exception cref="InvalidOperationException">Not a self-assessed kind, the reference is not revealed, or already graded.</exception>
    public void Skip()
    {
        EnsureAssessable();
        Skipped = true;
        IsFrozen = true;
    }

    public AttemptState Snapshot()
    {
        bool self = Kind == QuestionKind.SelfAssessed;
        return new AttemptState(
            ClientAnswerId,
            [.. Options.Select(o => o.Id)],
            [.. Options.Where(o => _picked.Contains(o.Id)).Select(o => o.Id)],
            [.. _lines.Order()],
            Typed,
            Notes,
            UsedHint,
            LastInputAt,
            IsFrozen,
            Grade is { } grade ? GradeKeys.ToKey(grade) : null,
            self ? null : AnsweredAt,
            SelfGrade is { } selfGrade ? GradeKeys.ToKey(selfGrade) : null,
            self ? AnsweredAt : null,
            Skipped,
            ReferenceRevealed);
    }

    /// <summary>
    /// Rebuilds an attempt exactly as saved. Content may have changed since: options keep their saved order, new ones
    /// follow, and picks or lines that no longer exist are dropped; an unreadable grade key counts as not graded.
    /// </summary>
    public static QuestionAttempt Restore(QuestionDto question, AttemptState state, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(state);

        QuestionAttempt attempt = new(question, state.ClientAnswerId, RestoreOrder(question.Options, state.OptionOrder), clock);
        foreach (string id in state.Picked)
        {
            if (attempt.Kind.IsChoice()
                && (attempt.Kind == QuestionKind.MultiChoice || attempt._picked.Count == 0)
                && question.Options.Any(o => string.Equals(o.Id, id, StringComparison.Ordinal)))
            {
                attempt._picked.Add(id);
            }
        }

        int lineCount = attempt.Kind == QuestionKind.FindBug ? question.CodeLinesHtml?.Count ?? 0 : 0;
        attempt._lines.UnionWith(state.Lines.Where(line => line >= 1 && line <= lineCount));
        attempt.Typed = state.Typed;
        attempt.Notes = state.Notes;
        attempt.UsedHint = state.UsedHint;
        attempt.LastInputAt = state.LastInputAt;
        attempt.IsFrozen = state.Frozen;
        attempt.Skipped = state.Skipped && attempt.Kind == QuestionKind.SelfAssessed;
        if (GradeKeys.TryParse(state.Grade, out Grade grade))
        {
            attempt.Grade = grade;
            attempt.AnsweredAt = attempt.Kind == QuestionKind.SelfAssessed ? state.AssessedAt : state.AnsweredAt;
        }

        if (GradeKeys.TryParse(state.SelfGrade, out Grade selfGrade))
        {
            attempt.SelfGrade = selfGrade;
        }

        attempt.ReferenceRevealed = state.ReferenceRevealed || attempt.Grade is not null || attempt.Skipped;
        return attempt;
    }

    private static QuestionDto Checked(QuestionDto question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return question;
    }

    private static List<OptionDto> RestoreOrder(IReadOnlyList<OptionDto> options, IReadOnlyList<string> savedOrder)
    {
        List<OptionDto> ordered = [];
        foreach (string id in savedOrder)
        {
            OptionDto? option = options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));
            if (option is not null && !ordered.Contains(option))
            {
                ordered.Add(option);
            }
        }

        ordered.AddRange(options.Where(o => !ordered.Contains(o)));
        return ordered;
    }

    private static bool HasTypedText(string value) => OutputText.Normalise(value).Length > 0;

    private string[] CorrectOptionIds() => [.. Options.Where(o => o.Correct).Select(o => o.Id)];

    // Content validation guarantees exactly one correct option; a question without one can only be answered wrongly.
    private Grade GradeSingle()
    {
        string picked = _picked.First();
        string? correct = Options.FirstOrDefault(o => o.Correct)?.Id;
        return correct is null ? AutoGrading.CapForHint(Domain.Grading.Grade.DidntKnow, UsedHint) : AutoGrading.Single(correct, picked, UsedHint);
    }

    private void EnsureAssessable()
    {
        if (Kind != QuestionKind.SelfAssessed)
        {
            throw new InvalidOperationException("Only open and situation questions are self-assessed.");
        }

        if (!ReferenceRevealed)
        {
            throw new InvalidOperationException("The reference answer must be shown before the self-assessment.");
        }

        if (IsDone || IsAssessing)
        {
            throw new InvalidOperationException("The attempt is already graded or skipped.");
        }
    }

    private void Complete(Grade grade, DateTimeOffset? at = null)
    {
        Grade = grade;
        AnsweredAt = at ?? _clock.Now();
        IsFrozen = true;
    }
}
