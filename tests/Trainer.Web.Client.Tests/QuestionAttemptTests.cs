using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Components;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class QuestionAttemptTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (QuestionAttempt Attempt, FakeTimeProvider Device, ServerClock Clock) Create(QuestionDto question, int seed = 1)
    {
        FakeTimeProvider device = new(DeviceStart);
        ServerClock clock = new(device, new FakeBrowserStore());
        return (new QuestionAttempt(question, TestQuestions.NewId(), new Random(seed), clock), device, clock);
    }

    [Theory]
    [InlineData("single", QuestionKind.SingleChoice)]
    [InlineData("multi", QuestionKind.MultiChoice)]
    [InlineData("find-bug", QuestionKind.FindBug)]
    [InlineData("open", QuestionKind.SelfAssessed)]
    [InlineData("situation", QuestionKind.SelfAssessed)]
    public void Kind_FollowsTheQuestionType(string type, QuestionKind kind)
    {
        QuestionDto question = type switch
        {
            "single" => TestQuestions.Single(),
            "multi" => TestQuestions.Multi(),
            "find-bug" => TestQuestions.FindBug(),
            _ => TestQuestions.Open(type),
        };

        Assert.Equal(kind, QuestionKinds.Of(question));
    }

    [Fact]
    public void Kind_OutputWithOptions_IsChoice_WithoutIsTyped()
    {
        Assert.Equal(QuestionKind.OutputChoice, Create(TestQuestions.OutputChoice()).Attempt.Kind);
        Assert.Equal(QuestionKind.OutputTyped, Create(TestQuestions.OutputTyped()).Attempt.Kind);
    }

    [Fact]
    public void Kind_UnknownType_Throws() =>
        Assert.Throws<ArgumentException>(() => QuestionKinds.Of(TestQuestions.Open("essay")));

    [Fact]
    public void Shuffle_IsPermutation_AndSeedDependent()
    {
        List<OptionDto> options = [.. Enumerable.Range(0, 8).Select(i => new OptionDto("o" + i, "", i == 0, null))];

        IReadOnlyList<OptionDto> first = OptionShuffler.Shuffle(options, new Random(1));
        IReadOnlyList<OptionDto> same = OptionShuffler.Shuffle(options, new Random(1));
        IReadOnlyList<OptionDto> other = OptionShuffler.Shuffle(options, new Random(2));

        Assert.Equal(options.Select(o => o.Id).Order(StringComparer.Ordinal), first.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.Equal(first.Select(o => o.Id), same.Select(o => o.Id));
        Assert.NotEqual(first.Select(o => o.Id), other.Select(o => o.Id));
        Assert.Equal(["o0", "o1", "o2", "o3", "o4", "o5", "o6", "o7"], options.Select(o => o.Id));
    }

    [Fact]
    public void Attempt_ShowsShuffledOptions()
    {
        QuestionDto question = TestQuestions.Single();
        HashSet<string> orders = [];
        for (int seed = 0; seed < 10; seed++)
        {
            QuestionAttempt attempt = Create(question, seed).Attempt;
            orders.Add(string.Join(',', attempt.Options.Select(o => o.Id)));
        }

        Assert.True(orders.Count > 1);
    }

    [Fact]
    public void Single_SubmitCorrect_IsKnew()
    {
        QuestionAttempt attempt = Create(TestQuestions.Single(correct: "b")).Attempt;

        attempt.Toggle("a");
        attempt.Toggle("b");
        Grade grade = attempt.SubmitAutomatic();

        Assert.Equal(Grade.Knew, grade);
        Assert.Equal(Grade.Knew, attempt.Grade);
        Assert.Equal(["b"], attempt.Picked);
        Assert.True(attempt.IsFrozen);
        Assert.True(attempt.IsDone);
    }

    [Fact]
    public void Single_SubmitWrong_IsDidntKnow()
    {
        QuestionAttempt attempt = Create(TestQuestions.Single(correct: "b")).Attempt;

        attempt.Toggle("c");

        Assert.Equal(Grade.DidntKnow, attempt.SubmitAutomatic());
    }

    [Fact]
    public void Hint_CapsAutomaticAtPartial()
    {
        QuestionAttempt attempt = Create(TestQuestions.Single(correct: "b")).Attempt;

        attempt.RevealHint();
        attempt.Toggle("b");

        Assert.True(attempt.UsedHint);
        Assert.Equal(Grade.Partial, attempt.SubmitAutomatic());
    }

    [Fact]
    public void Multi_ToggleAndSubmit_UsesSpecRule()
    {
        QuestionDto question = TestQuestions.Multi();

        QuestionAttempt exact = Create(question).Attempt;
        exact.Toggle("a");
        exact.Toggle("b");
        Assert.Equal(Grade.Knew, exact.SubmitAutomatic());

        QuestionAttempt oneMissed = Create(question).Attempt;
        oneMissed.Toggle("a");
        Assert.Equal(Grade.Partial, oneMissed.SubmitAutomatic());

        QuestionAttempt toggledOff = Create(question).Attempt;
        toggledOff.Toggle("a");
        toggledOff.Toggle("c");
        toggledOff.Toggle("d");
        toggledOff.Toggle("d");
        Assert.Equal(["a", "c"], toggledOff.Picked.Order(StringComparer.Ordinal));
        Assert.Equal(Grade.DidntKnow, toggledOff.SubmitAutomatic());
    }

    [Fact]
    public void Typed_SubmitNormalises()
    {
        QuestionAttempt attempt = Create(TestQuestions.OutputTyped("1\n2")).Attempt;

        attempt.SetTyped("  1\r\n2 \r\n");

        Assert.Equal(Grade.Knew, attempt.SubmitAutomatic());
    }

    [Fact]
    public void FindBug_ToggleLines_GradesBySet()
    {
        QuestionDto question = TestQuestions.FindBug();

        QuestionAttempt second = Create(question).Attempt;
        second.ToggleLine(3);
        second.ToggleLine(4);
        Assert.Equal(Grade.Knew, second.SubmitAutomatic());

        QuestionAttempt superset = Create(question).Attempt;
        superset.ToggleLine(2);
        superset.ToggleLine(3);
        Assert.Equal(Grade.DidntKnow, superset.SubmitAutomatic());

        QuestionAttempt toggledOff = Create(question).Attempt;
        toggledOff.ToggleLine(1);
        toggledOff.ToggleLine(2);
        toggledOff.ToggleLine(1);
        toggledOff.ToggleLine(99);
        Assert.Equal([2], toggledOff.SelectedLines);
        Assert.Equal(Grade.Knew, toggledOff.SubmitAutomatic());
    }

    [Fact]
    public async Task SelfAssessed_RequiresReveal_ThenGradesThroughGrader()
    {
        QuestionDto question = TestQuestions.Open("situation");
        QuestionAttempt attempt = Create(question).Attempt;
        RecordingGrader grader = new(Grade.Partial);
        attempt.SetNotes("restart the pods");
        attempt.RevealHint();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await attempt.SubmitSelfAssessmentAsync(Grade.Knew, grader, Ct));
        Assert.Null(grader.Received);
        Assert.Null(attempt.Grade);

        attempt.RevealReference();
        Grade grade = await attempt.SubmitSelfAssessmentAsync(Grade.Knew, grader, Ct);

        Assert.Equal(Grade.Partial, grade);
        Assert.Equal(Grade.Partial, attempt.Grade);
        Assert.Equal(Grade.Knew, attempt.SelfGrade);
        SelfGradedAnswer received = Assert.IsType<SelfGradedAnswer>(grader.Received);
        Assert.Equal(question.Id, received.QuestionId.Value);
        Assert.Equal(Domain.Questions.QuestionType.Situation, received.Type);
        Assert.Equal("restart the pods", received.Text);
        Assert.Equal(Grade.Knew, received.SelfAssessment);
        Assert.True(received.UsedHint);
        Assert.True(attempt.IsFrozen);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await attempt.SubmitSelfAssessmentAsync(Grade.DidntKnow, grader, Ct));
        Assert.Throws<InvalidOperationException>(() => attempt.SubmitAutomatic());
    }

    [Fact]
    public void CanSubmit_FalseUntilSomethingPicked()
    {
        QuestionAttempt single = Create(TestQuestions.Single()).Attempt;
        Assert.False(single.CanSubmit);
        Assert.Throws<InvalidOperationException>(() => single.SubmitAutomatic());
        single.Toggle("a");
        Assert.True(single.CanSubmit);
        single.SubmitAutomatic();
        Assert.False(single.CanSubmit);

        QuestionAttempt typed = Create(TestQuestions.OutputTyped()).Attempt;
        typed.SetTyped("   ");
        Assert.False(typed.CanSubmit);
        typed.SetTyped("3");
        Assert.True(typed.CanSubmit);

        QuestionAttempt lines = Create(TestQuestions.FindBug()).Attempt;
        Assert.False(lines.CanSubmit);
        lines.ToggleLine(1);
        Assert.True(lines.CanSubmit);

        QuestionAttempt open = Create(TestQuestions.Open()).Attempt;
        open.SetNotes("notes");
        open.RevealReference();
        Assert.False(open.CanSubmit);
    }

    [Fact]
    public void LastInputAt_UpdatedByPicksLinesAndTyped_NotByHintOrNotes()
    {
        (QuestionAttempt single, FakeTimeProvider device, ServerClock clock) = Create(TestQuestions.Single());
        single.RevealHint();
        single.SetNotes("thinking");
        Assert.Null(single.LastInputAt);

        device.Advance(TimeSpan.FromSeconds(5));
        single.Toggle("a");
        Assert.Equal(clock.Now(), single.LastInputAt);

        device.Advance(TimeSpan.FromSeconds(5));
        single.RevealHint();
        single.SetNotes("more");
        Assert.Equal(DeviceStart.AddSeconds(5), single.LastInputAt);

        (QuestionAttempt lines, FakeTimeProvider linesDevice, _) = Create(TestQuestions.FindBug());
        linesDevice.Advance(TimeSpan.FromSeconds(7));
        lines.ToggleLine(2);
        Assert.Equal(DeviceStart.AddSeconds(7), lines.LastInputAt);

        (QuestionAttempt typed, FakeTimeProvider typedDevice, _) = Create(TestQuestions.OutputTyped());
        typed.SetTyped("");
        Assert.Null(typed.LastInputAt);
        typedDevice.Advance(TimeSpan.FromSeconds(9));
        typed.SetTyped("3");
        Assert.Equal(DeviceStart.AddSeconds(9), typed.LastInputAt);
        typedDevice.Advance(TimeSpan.FromSeconds(1));
        typed.SetTyped("");
        Assert.Equal(DeviceStart.AddSeconds(9), typed.LastInputAt);
    }

    [Fact]
    public void HintOnly_IsNotAResponse()
    {
        QuestionAttempt attempt = Create(TestQuestions.Single()).Attempt;

        attempt.RevealHint();

        Assert.False(attempt.HasResponse);
        Assert.False(attempt.CanSubmit);
        Assert.Throws<InvalidOperationException>(() => attempt.SubmitAutomatic());

        // A question without a hint cannot have its grade capped by one.
        QuestionAttempt noHint = Create(TestQuestions.Single(hint: null)).Attempt;
        noHint.RevealHint();
        Assert.False(noHint.UsedHint);
    }

    [Fact]
    public async Task SelfAssessed_BlankNotes_ExplicitGradeCreatesAnswer()
    {
        (QuestionAttempt attempt, _, ServerClock clock) = Create(TestQuestions.Open());
        Assert.False(attempt.HasResponse);

        attempt.RevealReference();
        Grade grade = await attempt.SubmitSelfAssessmentAsync(Grade.DidntKnow, new SelfAssessmentGrader(), Ct);

        Assert.Equal(Grade.DidntKnow, grade);
        Assert.True(attempt.HasResponse);
        Assert.Equal(clock.Now(), attempt.AnsweredAt);
        AnswerDto answer = new AnswerFactory().Create(attempt, AnswerMode.Practice, null);
        Assert.Equal("didnt-know", answer.Grade);
    }

    [Fact]
    public void SelfAssessed_Skip_CreatesNoAnswer()
    {
        QuestionAttempt attempt = Create(TestQuestions.Open()).Attempt;
        attempt.SetNotes("no idea");

        Assert.Throws<InvalidOperationException>(attempt.Skip);
        attempt.RevealReference();
        attempt.Skip();

        Assert.True(attempt.Skipped);
        Assert.True(attempt.IsDone);
        Assert.True(attempt.IsFrozen);
        Assert.Null(attempt.Grade);
        Assert.Null(attempt.AnsweredAt);
        Assert.False(attempt.HasResponse);
        Assert.Throws<InvalidOperationException>(() => new AnswerFactory().Create(attempt, AnswerMode.Practice, null));
    }

    [Fact]
    public void Skip_OnAutomaticKind_Throws()
    {
        QuestionAttempt attempt = Create(TestQuestions.Single()).Attempt;
        attempt.RevealReference();

        Assert.Throws<InvalidOperationException>(attempt.Skip);
    }

    [Fact]
    public async Task AnsweredAt_UsesServerClock()
    {
        (QuestionAttempt single, FakeTimeProvider device, ServerClock clock) = Create(TestQuestions.Single());
        // The server is an hour ahead of the device: the stamp must follow the server.
        clock.Observe(DeviceStart.AddHours(1), DeviceStart, DeviceStart);
        device.Advance(TimeSpan.FromSeconds(30));
        single.Toggle("a");
        device.Advance(TimeSpan.FromSeconds(30));

        single.SubmitAutomatic();

        Assert.Equal(DeviceStart.AddHours(1).AddMinutes(1).AddMilliseconds(500), single.AnsweredAt);
        Assert.Equal(clock.Now(), single.AnsweredAt);

        QuestionAttempt open = new(TestQuestions.Open(), TestQuestions.NewId(), new Random(1), clock);
        open.RevealReference();
        device.Advance(TimeSpan.FromSeconds(10));
        await open.SubmitSelfAssessmentAsync(Grade.Knew, new SelfAssessmentGrader(), Ct);
        Assert.Equal(clock.Now(), open.AnsweredAt);
    }

    [Fact]
    public void Freeze_IgnoresFurtherInput()
    {
        QuestionAttempt choice = Create(TestQuestions.Multi()).Attempt;
        choice.Toggle("a");
        choice.Freeze();
        choice.Toggle("b");
        choice.Toggle("a");
        choice.RevealHint();
        choice.SetNotes("late");

        Assert.Equal(["a"], choice.Picked);
        Assert.False(choice.UsedHint);
        Assert.Equal("", choice.Notes);
        Assert.False(choice.CanSubmit);
        // A frozen response is still graded (Mock grades at Finish).
        Assert.Equal(Grade.Partial, choice.SubmitAutomatic());

        QuestionAttempt lines = Create(TestQuestions.FindBug()).Attempt;
        lines.Freeze();
        lines.ToggleLine(2);
        Assert.Empty(lines.SelectedLines);

        QuestionAttempt typed = Create(TestQuestions.OutputTyped()).Attempt;
        typed.SetTyped("1");
        typed.Freeze();
        typed.SetTyped("333");
        Assert.Equal("1", typed.Typed);
    }

    [Fact]
    public async Task Freeze_StillAllowsSelfAssessment()
    {
        QuestionAttempt open = Create(TestQuestions.Open()).Attempt;
        open.SetNotes("my plan");
        open.Freeze();
        open.SetNotes("changed");
        open.RevealReference();

        Grade grade = await open.SubmitSelfAssessmentAsync(Grade.Knew, new SelfAssessmentGrader(), Ct);

        Assert.Equal("my plan", open.Notes);
        Assert.Equal(Grade.Knew, grade);
    }

    [Fact]
    public void SnapshotRestore_KeepsIdOptionOrderPicksNotesFrozenGradeAndSkip()
    {
        QuestionDto question = TestQuestions.Multi();
        (QuestionAttempt attempt, FakeTimeProvider device, ServerClock clock) = Create(question, seed: 7);
        attempt.RevealHint();
        device.Advance(TimeSpan.FromSeconds(3));
        attempt.Toggle("b");
        attempt.Toggle("a");
        attempt.SetNotes("notes");
        device.Advance(TimeSpan.FromSeconds(3));
        attempt.SubmitAutomatic();

        AttemptState state = attempt.Snapshot();
        QuestionAttempt restored = QuestionAttempt.Restore(question, state, clock);

        Assert.Equal(attempt.ClientAnswerId, restored.ClientAnswerId);
        Assert.Equal(attempt.Options.Select(o => o.Id), restored.Options.Select(o => o.Id));
        Assert.Equal(attempt.Options.Select(o => o.Id), state.OptionOrder);
        Assert.Equal(attempt.Picked.Order(StringComparer.Ordinal), restored.Picked.Order(StringComparer.Ordinal));
        Assert.Equal("notes", restored.Notes);
        Assert.True(restored.UsedHint);
        Assert.True(restored.IsFrozen);
        Assert.Equal(Grade.Partial, restored.Grade);
        Assert.Equal("partial", state.Grade);
        Assert.Equal(attempt.AnsweredAt, restored.AnsweredAt);
        Assert.Equal(DeviceStart.AddSeconds(3), restored.LastInputAt);
        Assert.False(restored.Skipped);

        QuestionDto open = TestQuestions.Open();
        QuestionAttempt skipped = new(open, TestQuestions.NewId(), new Random(1), clock);
        skipped.SetNotes("hmm");
        skipped.RevealReference();
        skipped.Skip();
        QuestionAttempt restoredSkip = QuestionAttempt.Restore(open, skipped.Snapshot(), clock);
        Assert.True(restoredSkip.Skipped);
        Assert.True(restoredSkip.IsFrozen);
        Assert.Equal("hmm", restoredSkip.Notes);
        Assert.Null(restoredSkip.Grade);

        QuestionDto lines = TestQuestions.FindBug();
        QuestionAttempt bug = new(lines, TestQuestions.NewId(), new Random(1), clock);
        bug.ToggleLine(4);
        bug.ToggleLine(3);
        AttemptState bugState = bug.Snapshot();
        Assert.Equal([3, 4], bugState.Lines);
        QuestionAttempt restoredBug = QuestionAttempt.Restore(lines, bugState, clock);
        Assert.Equal([3, 4], restoredBug.SelectedLines.Order());
        Assert.False(restoredBug.IsFrozen);
        Assert.True(restoredBug.CanSubmit);
    }

    [Fact]
    public async Task SnapshotRestore_SelfAssessed_KeepsSelfGradeAndAssessedTime()
    {
        QuestionDto question = TestQuestions.Open();
        (QuestionAttempt attempt, _, ServerClock clock) = Create(question);
        attempt.RevealHint();
        attempt.RevealReference();
        await attempt.SubmitSelfAssessmentAsync(Grade.Knew, new SelfAssessmentGrader(), Ct);

        AttemptState state = attempt.Snapshot();
        QuestionAttempt restored = QuestionAttempt.Restore(question, state, clock);

        Assert.Equal("knew", state.SelfGrade);
        Assert.Equal("partial", state.Grade);
        Assert.Equal(attempt.AnsweredAt, state.AssessedAt);
        Assert.Null(state.AnsweredAt);
        Assert.Equal(Grade.Partial, restored.Grade);
        Assert.Equal(Grade.Knew, restored.SelfGrade);
        Assert.Equal(attempt.AnsweredAt, restored.AnsweredAt);
        Assert.True(restored.ReferenceRevealed);
    }

    [Fact]
    public void Restore_ContentChanged_KeepsKnownOptionsAndDropsUnknownPicks()
    {
        QuestionDto question = TestQuestions.Single();
        (_, _, ServerClock clock) = Create(question);
        AttemptState state = new(
            TestQuestions.NewId(), ["d", "gone", "a"], ["gone", "d"], [], "", "", false, null, false, "bogus", null, null, null, false);

        QuestionAttempt restored = QuestionAttempt.Restore(question, state, clock);

        Assert.Equal(["d", "a", "b", "c"], restored.Options.Select(o => o.Id));
        Assert.Equal(["d"], restored.Picked);
        Assert.Null(restored.Grade);
    }

    [Fact]
    public async Task SelfAssessment_WhileGrading_IsVisibleAndASecondGradeIsRefused()
    {
        QuestionAttempt attempt = Create(TestQuestions.Open()).Attempt;
        attempt.RevealReference();
        PendingGrader grader = new();
        Assert.True(attempt.CanSelfAssess);

        ValueTask<Grade> first = attempt.SubmitSelfAssessmentAsync(Grade.Knew, grader, Ct);

        Assert.True(attempt.IsAssessing);
        Assert.False(attempt.CanSelfAssess);
        Assert.Equal(ShortcutCommand.None, KeyboardShortcuts.Resolve(ShortcutKey.Option2, attempt, false).Command);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await attempt.SubmitSelfAssessmentAsync(Grade.DidntKnow, grader, Ct));
        Assert.Throws<InvalidOperationException>(attempt.Skip);

        grader.Complete(Grade.Knew);
        Assert.Equal(Grade.Knew, await first);
        Assert.False(attempt.IsAssessing);
        Assert.False(attempt.CanSelfAssess);
        Assert.Equal(1, grader.Calls);
        Assert.Equal(Grade.Knew, attempt.Grade);
    }

    [Fact]
    public void Hint_AfterReferenceRevealed_IsIgnored()
    {
        QuestionAttempt attempt = Create(TestQuestions.Open()).Attempt;
        attempt.RevealReference();

        attempt.RevealHint();

        Assert.False(attempt.UsedHint);
    }

    [Fact]
    public void SnapshotRestore_KeepsARevealedReferenceBeforeGrading()
    {
        QuestionDto question = TestQuestions.Open();
        (QuestionAttempt attempt, _, ServerClock clock) = Create(question);
        AttemptState hidden = attempt.Snapshot();
        attempt.RevealReference();

        AttemptState state = attempt.Snapshot();
        QuestionAttempt restored = QuestionAttempt.Restore(question, state, clock);

        Assert.False(hidden.ReferenceRevealed);
        Assert.True(state.ReferenceRevealed);
        Assert.True(restored.ReferenceRevealed);
        Assert.Null(restored.Grade);
        Assert.True(restored.CanSelfAssess);
        Assert.False(QuestionAttempt.Restore(question, hidden, clock).ReferenceRevealed);
    }

    private sealed class PendingGrader : IAnswerGrader
    {
        private readonly TaskCompletionSource<Grade> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public void Complete(Grade grade) => _result.SetResult(grade);

        public async ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct)
        {
            Calls++;
            return await _result.Task;
        }
    }

    private sealed class RecordingGrader(Grade result) : IAnswerGrader
    {
        public SelfGradedAnswer? Received { get; private set; }

        public ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct)
        {
            Received = answer;
            return ValueTask.FromResult(result);
        }
    }
}
