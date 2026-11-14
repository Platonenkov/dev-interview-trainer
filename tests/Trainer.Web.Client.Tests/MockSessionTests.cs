using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class MockSessionTests
{
    private const long TelegramId = 4242;
    private static readonly string Owner = TelegramId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public Rig(bool timed = false, string owner = "4242", params QuestionDto[] questions)
        {
            Device = new FakeTimeProvider(DeviceStart);
            Clock = new ServerClock(Device, new FakeBrowserStore());
            Ids = new UlidGenerator(Device);
            Questions = questions;
            Context = new MockContext(
                Ids.Next(), owner, "dotnet", "en", "middle", timed,
                timed ? MockContext.PerQuestion * questions.Length : null, Clock.Now());
            Session = new MockSession(questions, Context, Ids, new Random(7), Clock);
        }

        public FakeTimeProvider Device { get; }

        public ServerClock Clock { get; }

        public UlidGenerator Ids { get; }

        public QuestionDto[] Questions { get; }

        public MockContext Context { get; }

        public MockSession Session { get; }
    }

    private static readonly SelfAssessmentGrader Grader = new();

    private static void PickCorrect(QuestionAttempt attempt) =>
        attempt.Toggle(attempt.Options.First(o => o.Correct).Id);

    private static string Json(MockProgress progress) => JsonSerializer.Serialize(progress, TrainerJsonContext.Default.MockProgress);

    [Theory]
    [InlineData(20, 7, true)]
    [InlineData(20, 5, true)]
    [InlineData(7, 7, false)]
    [InlineData(20, 0, false)]
    // A pool of 1-4 suggests its own size, which is no mock count: the session already takes the whole pool.
    [InlineData(5, 3, false)]
    [InlineData(20, 4, false)]
    public void Settings_OfferOnlyASuggestionThatIsAValidSmallerCount(int count, int suggested, bool offered)
    {
        MockSettings settings = new("middle", [], count, false);

        Assert.Equal(offered, settings.OffersSuggestion(suggested));
    }

    [Fact]
    public void Finish_GradesAutomaticWithResponseAndListsAllSelfAssessed()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open(), TestQuestions.Multi(), TestQuestions.Open("situation"));
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);
        session.Attempts[1].SetNotes("my notes");

        Assert.Equal(MockPhase.Answering, session.Phase);
        Assert.True(session.Finish());

        Assert.Equal(MockPhase.Assessment, session.Phase);
        Assert.Equal(Grade.Knew, session.Attempts[0].Grade);
        Assert.Null(session.Attempts[2].Grade);
        Assert.All(session.Attempts, a => Assert.True(a.IsFrozen));
        Assert.Equal([session.Attempts[1], session.Attempts[3]], session.PendingAssessment);
        Assert.All(session.PendingAssessment, a => Assert.True(a.ReferenceRevealed));
    }

    [Fact]
    public async Task Finish_OpenWithoutNotes_IsAssessedAndRecorded()
    {
        Rig rig = new(false, Owner, TestQuestions.Open());
        MockSession session = rig.Session;
        session.Finish();

        await session.AssessAsync(0, Grade.Partial, Grader, Ct);
        MockCompletionState completion = session.Complete();

        AnswerDto answer = Assert.Single(completion.Answers);
        Assert.Equal(session.Attempts[0].ClientAnswerId, answer.ClientAnswerId);
        Assert.Equal("partial", answer.Grade);
        Assert.Equal("mock", answer.Mode);
        Assert.Equal(rig.Context.SessionId, answer.MockSessionId);
        Assert.Equal(MockPhase.Summary, session.Phase);
    }

    [Fact]
    public void Assessment_Skip_CountsUnanswered()
    {
        Rig rig = new(false, Owner, TestQuestions.Open());
        MockSession session = rig.Session;
        session.Finish();

        session.SkipAssessment(0);
        MockCompletionState completion = session.Complete();

        Assert.Empty(completion.Answers);
        MockTopicResultDto topic = Assert.Single(completion.Summary.Topics);
        Assert.Equal(1, topic.Unanswered);
        Assert.Equal(0, topic.Knew + topic.Partial + topic.DidntKnow);
    }

    [Fact]
    public void HintOnlyAutomatic_IsUnanswered()
    {
        Rig rig = new(false, Owner, TestQuestions.Single());
        MockSession session = rig.Session;
        session.Attempts[0].RevealHint();

        session.Finish();
        MockCompletionState completion = session.Complete();

        Assert.Null(session.Attempts[0].Grade);
        Assert.Empty(completion.Answers);
        Assert.Equal(1, Assert.Single(completion.Summary.Topics).Unanswered);
    }

    [Fact]
    public void Finish_IsIdempotent()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);

        Assert.True(session.Finish());
        DateTimeOffset finishedAt = session.FinishedAt!.Value;
        rig.Device.Advance(TimeSpan.FromSeconds(5));
        Assert.False(session.Finish());

        Assert.Equal(finishedAt, session.FinishedAt);
        Assert.Equal(Grade.Knew, session.Attempts[0].Grade);
        session.Attempts[0].Toggle("b");
        Assert.Equal(["a"], session.Attempts[0].Picked);
    }

    [Fact]
    public async Task FinishAndTimerAtOnce_CompleteOnce()
    {
        Rig rig = new(true, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Multi(), TestQuestions.FindBug());
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);
        session.Attempts[1].Toggle("a");

        bool[] finished = await Task.WhenAll(Task.Run(session.Finish, Ct), Task.Run(session.Finish, Ct));
        MockCompletionState[] completions = await Task.WhenAll(Task.Run(session.Complete, Ct), Task.Run(session.Complete, Ct));

        Assert.Single(finished, f => f);
        Assert.Same(completions[0], completions[1]);
        Assert.Equal(
            [session.Attempts[0].ClientAnswerId, session.Attempts[1].ClientAnswerId],
            completions[0].Answers.Select(a => a.ClientAnswerId));
    }

    [Fact]
    public async Task AnsweredAt_IsLastInputForAutomatic_AssessmentForSelf()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open());
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);
        DateTimeOffset picked = rig.Clock.Now();
        rig.Device.Advance(TimeSpan.FromMinutes(3));
        session.Finish();
        rig.Device.Advance(TimeSpan.FromMinutes(2));
        DateTimeOffset assessed = rig.Clock.Now();

        await session.AssessAsync(1, Grade.Knew, Grader, Ct);
        MockCompletionState completion = session.Complete();

        Assert.Equal(picked, completion.Answers[0].AnsweredAt);
        Assert.Equal(assessed, completion.Answers[1].AnsweredAt);
    }

    [Fact]
    public void Remaining_ComputedFromDeadline()
    {
        Rig timed = new(true, Owner, TestQuestions.Single(), TestQuestions.Single(), TestQuestions.Single(), TestQuestions.Single(), TestQuestions.Single());
        Rig untimed = new(false, Owner, TestQuestions.Single());
        DateTimeOffset start = timed.Context.StartedAt;

        Assert.Equal(start + TimeSpan.FromSeconds(450), timed.Session.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(350), timed.Session.Remaining(start.AddSeconds(100)));
        Assert.Equal(TimeSpan.Zero, timed.Session.Remaining(start.AddSeconds(500)));
        Assert.False(timed.Session.IsExpired(start.AddSeconds(449)));
        Assert.True(timed.Session.IsExpired(start.AddSeconds(450)));
        Assert.Null(untimed.Session.Deadline);
        Assert.False(untimed.Session.IsExpired(start.AddDays(1)));
        Assert.Equal(Timeout.InfiniteTimeSpan, untimed.Session.Remaining(start));
    }

    [Fact]
    public void NoGradeVisibleBeforeFinish()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.FindBug(), TestQuestions.OutputTyped("1"));
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);
        session.Attempts[1].ToggleLine(2);
        session.Attempts[2].SetTyped("1");

        Assert.All(session.Attempts, a => Assert.Null(a.Grade));
        Assert.All(session.Snapshot(1).Attempts, a => Assert.Null(a.Grade));
        Assert.Null(session.Snapshot(1).Completion);
        Assert.All(session.Attempts, a => Assert.False(a.IsFrozen));
        Assert.All(session.Attempts, a => Assert.True(MockSession.IsAnswered(a)));
    }

    [Fact]
    public async Task SnapshotRestore_KeepsFrozenGradesCompletionAndContext()
    {
        Rig rig = new(true, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open(), TestQuestions.Open("situation"));
        MockSession session = rig.Session;
        PickCorrect(session.Attempts[0]);
        session.Attempts[1].SetNotes("notes");
        session.GoTo(2);
        session.Finish();
        await session.AssessAsync(1, Grade.DidntKnow, Grader, Ct);

        MockProgress assessing = session.Snapshot(5);
        MockSession restored = MockSession.Restore(assessing, rig.Questions, rig.Clock);

        Assert.Equal(MockPhase.Assessment, restored.Phase);
        Assert.Equal(2, restored.Index);
        Assert.Equal(session.Deadline, restored.Deadline);
        Assert.Equal(session.FinishedAt, restored.FinishedAt);
        Assert.Equal(rig.Context, restored.Context);
        Assert.Equal(Grade.Knew, restored.Attempts[0].Grade);
        Assert.Equal(Grade.DidntKnow, restored.Attempts[1].Grade);
        Assert.Equal("notes", restored.Attempts[1].Notes);
        Assert.All(restored.Attempts, a => Assert.True(a.IsFrozen));
        Assert.Equal([restored.Attempts[2]], restored.PendingAssessment);
        Assert.False(restored.Finish());
        MockProgress again5 = restored.Snapshot(5);
        Assert.Equal(Json(assessing with { Attempts = [] }), Json(again5 with { Attempts = [] }));
        Assert.Equal(assessing.Attempts.Select(a => (a.ClientAnswerId, a.Grade, a.AnsweredAt, a.AssessedAt, a.Frozen)),
            again5.Attempts.Select(a => (a.ClientAnswerId, a.Grade, a.AnsweredAt, a.AssessedAt, a.Frozen)));

        restored.SkipAssessment(2);
        MockCompletionState completion = restored.Complete();
        MockProgress done = restored.Snapshot(6);
        MockSession again = MockSession.Restore(done, rig.Questions, rig.Clock);

        Assert.Equal(MockPhase.Summary, again.Phase);
        MockCompletionState stored = again.Complete();
        Assert.Same(stored, again.Complete());
        Assert.Equal(
            JsonSerializer.Serialize(completion, TrainerJsonContext.Default.MockCompletionState),
            JsonSerializer.Serialize(stored, TrainerJsonContext.Default.MockCompletionState));
    }

    [Fact]
    public void Restore_QuestionsDoNotMatch_Throws()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(), TestQuestions.Open());

        Assert.Throws<ArgumentException>(() => MockSession.Restore(rig.Session.Snapshot(1), [rig.Questions[0]], rig.Clock));
    }

    [Fact]
    public async Task ActiveMock_InputSave_DoesNotRewriteQuestions()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(), TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, rig.Device);

        Assert.True(await persistence.StartAsync(rig.Session, TelegramId));
        rig.Session.Attempts[0].Toggle("a");
        await persistence.SaveAsync(rig.Session);
        rig.Session.GoTo(1);
        await persistence.SaveAsync(rig.Session);

        Assert.Single(fake.Ops, op => op.Op == "put" && op.Key == StorageKeys.MockQuestions(Owner, rig.Context.SessionId));
        Assert.Equal(3, fake.Ops.Count(op => op.Op == "putIfRevision"));
        Assert.Equal(3, persistence.Revision);

        (MockSession? resumed, _) = await Persistence(fake, rig.Device).LoadAsync(Owner, rig.Clock);
        Assert.NotNull(resumed);
        Assert.Equal(1, resumed.Index);
        Assert.Equal(["a"], resumed.Attempts[0].Picked);
    }

    [Fact]
    public async Task TypedInput_IsDebouncedAndFlushedOnPagehide()
    {
        Rig rig = new(false, Owner, TestQuestions.OutputTyped("1"), TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, rig.Device);
        await persistence.StartAsync(rig.Session, TelegramId);
        int Saves() => fake.Ops.Count(op => op.Op == "putIfRevision");

        rig.Session.Attempts[0].SetTyped("1");
        persistence.ScheduleSave(rig.Session);
        rig.Device.Advance(TimeSpan.FromMilliseconds(300));
        rig.Session.Attempts[0].SetTyped("12");
        persistence.ScheduleSave(rig.Session);
        rig.Device.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, Saves());

        rig.Device.Advance(TimeSpan.FromMilliseconds(200));
        await persistence.Idle;
        Assert.Equal(2, Saves());

        rig.Session.Attempts[1].SetNotes("thinking");
        persistence.ScheduleSave(rig.Session);
        Assert.Equal(2, Saves());
        await persistence.FlushAsync();
        Assert.Equal(3, Saves());
        rig.Device.Advance(TimeSpan.FromSeconds(1));
        await persistence.Idle;
        Assert.Equal(3, Saves());

        (MockSession? resumed, _) = await Persistence(fake, rig.Device).LoadAsync(Owner, rig.Clock);
        Assert.Equal("12", resumed!.Attempts[0].Typed);
        Assert.Equal("thinking", resumed.Attempts[1].Notes);
    }

    public static TheoryData<string> CrashPoints => ["complete", "append", "summaryAdd", "delete"];

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task Handoff_TerminatedAfterEachStep_ResumesWithoutLossOrDuplicates(string crashPoint)
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open(), TestQuestions.Multi());
        FakeBrowserStore fake = new();
        CrashingStore crashing = new(fake, crashPoint);
        MockSession session = rig.Session;
        using (MockPersistence first = Persistence(crashing, rig.Device))
        {
            await first.StartAsync(session, TelegramId);
            PickCorrect(session.Attempts[0]);
            session.Finish();
            await first.SaveAsync(session);
            await session.AssessAsync(1, Grade.Knew, Grader, Ct);
            await first.SaveAsync(session);

            await Assert.ThrowsAsync<InvalidOperationException>(() => first.HandOffAsync(session, TelegramId));
        }

        Assert.True(crashing.Crashed);
        using MockPersistence second = Persistence(fake, rig.Device);
        MockSession? resumed = await LoadAdoptedAsync(second, rig.Clock);
        Assert.NotNull(resumed);
        Assert.Equal(MockPhase.Summary, resumed.Phase);

        MockHandoff outcome = await second.HandOffAsync(resumed, TelegramId);

        Assert.True(outcome.Cleared);
        string[] stored = Ids(fake.Raw(StorageKeys.UserOutbox(TelegramId)), "answers", "clientAnswerId");
        Assert.Equal([session.Attempts[0].ClientAnswerId, session.Attempts[1].ClientAnswerId], stored);
        Assert.Equal([rig.Context.SessionId], Ids(fake.Raw(StorageKeys.PendingSummaries(TelegramId)), "items", "mockSessionId"));
        Assert.Null(fake.Raw(StorageKeys.MockProgress(Owner)));
        Assert.Null(fake.Raw(StorageKeys.MockQuestions(Owner, rig.Context.SessionId)));
    }

    [Fact]
    public async Task StaleDebouncedSave_OfAnEarlierSession_DoesNotTurnTheNewMockReadOnly()
    {
        Rig earlier = new(false, Owner, TestQuestions.OutputTyped("1"));
        Rig later = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, earlier.Device);
        await persistence.StartAsync(earlier.Session, TelegramId);
        earlier.Session.Attempts[0].SetTyped("1");
        earlier.Session.Finish();
        Assert.True((await persistence.HandOffAsync(earlier.Session, TelegramId)).Cleared);
        // A late keystroke of the finished mock leaves a debounced save behind; the next mock starts before it fires.
        persistence.ScheduleSave(earlier.Session);
        Assert.True(await persistence.StartAsync(later.Session, TelegramId));
        int savesBefore = fake.Ops.Count(op => op.Op == "putIfRevision");

        earlier.Device.Advance(TimeSpan.FromSeconds(1));
        await persistence.Idle;

        Assert.False(persistence.IsReadOnly);
        Assert.Equal(savesBefore, fake.Ops.Count(op => op.Op == "putIfRevision"));
        Assert.True(await persistence.SaveAsync(later.Session));
    }

    [Fact]
    public async Task Handoff_Anonymous_ShowsSummaryWithoutQueuingIt()
    {
        Rig rig = new(false, StorageKeys.AnonymousOwner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, rig.Device);
        await persistence.StartAsync(rig.Session, null);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();

        MockHandoff outcome = await persistence.HandOffAsync(rig.Session, null);

        Assert.True(outcome.Cleared);
        Assert.True(rig.Session.Completion!.SummaryQueued);
        Assert.DoesNotContain(fake.Ops, op => op.Op == "summaryAdd");
        Assert.Single(Ids(fake.Raw(StorageKeys.AnonymousOutbox), "answers", "clientAnswerId"));
    }

    [Fact]
    public async Task Handoff_MemoryOnly_NeverClears()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new() { LocksAvailable = false };
        using MockPersistence persistence = Persistence(fake, rig.Device, out OutboxStore outbox);
        await persistence.StartAsync(rig.Session, TelegramId);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();

        MockHandoff outcome = await persistence.HandOffAsync(rig.Session, null);

        Assert.False(outcome.Cleared);
        Assert.False(outcome.AnswersPersisted);
        Assert.DoesNotContain(fake.Ops, op => op.Op == "delete");
        Assert.Single(await outbox.ReadAsync(StorageKeys.AnonymousOutbox));
        Assert.Equal(StorageStatus.MemoryOnly, outbox.Status);
    }

    [Fact]
    public async Task Handoff_MemoryOnlySignedIn_HoldsSummaryForSync()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new() { LocksAvailable = false };
        using MockPersistence persistence = Persistence(fake, rig.Device, out OutboxStore outbox, out SummaryStore summaries);
        await persistence.StartAsync(rig.Session, TelegramId);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();

        MockHandoff outcome = await persistence.HandOffAsync(rig.Session, TelegramId);

        // The answers are only in memory and the sync service sends them; the summary must travel with them.
        Assert.False(outcome.Cleared);
        Assert.Single(await outbox.ReadAsync(StorageKeys.UserOutbox(TelegramId)));
        StoredMockSummary held = Assert.Single(await summaries.ReadPendingAsync(TelegramId));
        Assert.Equal(rig.Context.SessionId, held.MockSessionId);
    }

    [Fact]
    public async Task Handoff_QuotaFull_HoldsSummaryWithoutStoringItBeforeTheAnswers()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, rig.Device, out OutboxStore outbox, out SummaryStore summaries);
        await persistence.StartAsync(rig.Session, TelegramId);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();
        fake.QuotaFull = true;

        await persistence.HandOffAsync(rig.Session, TelegramId);

        // Ordered: a stored summary must never count answers that are not stored, so it waits in memory with them.
        Assert.DoesNotContain(fake.Ops, op => op.Op == "summaryAdd");
        Assert.Single(await summaries.ReadPendingAsync(TelegramId));
        Assert.False(rig.Session.Completion!.SummaryQueued);
        Assert.Equal(1, outbox.UnsavedCount);
    }

    [Fact]
    public async Task AnonymousMock_None_ReportsNone()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using MockPersistence signedIn = Persistence(fake, rig.Device);

        Assert.Equal(AnonymousMockState.None, await signedIn.ReleaseAnonymousAsync(rig.Clock, finish: false));
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public async Task AnonymousMock_CompletedBeforeSignIn_IsHandedToTheAnonymousOutbox()
    {
        Rig anonymous = new(false, StorageKeys.AnonymousOwner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using (MockPersistence before = Persistence(fake, anonymous.Device))
        {
            await before.StartAsync(anonymous.Session, null);
            PickCorrect(anonymous.Session.Attempts[0]);
            anonymous.Session.Finish();
            anonymous.Session.Complete();
            // The page was closed before the hand-over: the answers sit only inside the progress document.
            Assert.True(await before.SaveAsync(anonymous.Session));
        }

        using MockPersistence signedIn = Persistence(fake, anonymous.Device);

        Assert.Equal(AnonymousMockState.HandedOver, await signedIn.ReleaseAnonymousAsync(anonymous.Clock, finish: false));

        // The import moves the anonymous outbox, so the answers become reachable from the profile.
        Assert.Single(Ids(fake.Raw(StorageKeys.AnonymousOutbox), "answers", "clientAnswerId"));
        Assert.Null(fake.Raw(StorageKeys.MockProgress(StorageKeys.AnonymousOwner)));
        Assert.Null(fake.Raw(StorageKeys.MockQuestions(StorageKeys.AnonymousOwner, anonymous.Context.SessionId)));
        Assert.DoesNotContain(fake.Ops, op => op.Op == "summaryAdd");
    }

    [Fact]
    public async Task AnonymousMock_Unfinished_IsOnlyReportedUntilTheUserEndsIt()
    {
        Rig anonymous = new(false, StorageKeys.AnonymousOwner, TestQuestions.Single(correct: "a"), TestQuestions.Open());
        FakeBrowserStore fake = new();
        using (MockPersistence before = Persistence(fake, anonymous.Device))
        {
            await before.StartAsync(anonymous.Session, null);
            PickCorrect(anonymous.Session.Attempts[0]);
            Assert.True(await before.SaveAsync(anonymous.Session));
        }

        using MockPersistence signedIn = Persistence(fake, anonymous.Device);
        int writes = fake.Writes;

        Assert.Equal(AnonymousMockState.Unfinished, await signedIn.ReleaseAnonymousAsync(anonymous.Clock, finish: false));
        Assert.Equal(writes, fake.Writes);
        Assert.NotNull(fake.Raw(StorageKeys.MockProgress(StorageKeys.AnonymousOwner)));

        Assert.Equal(AnonymousMockState.HandedOver, await signedIn.ReleaseAnonymousAsync(anonymous.Clock, finish: true));

        // Only the graded answer is handed over; the open question is skipped, exactly as "End it" does elsewhere.
        Assert.Single(Ids(fake.Raw(StorageKeys.AnonymousOutbox), "answers", "clientAnswerId"));
        Assert.Null(fake.Raw(StorageKeys.MockProgress(StorageKeys.AnonymousOwner)));
        Assert.Equal(AnonymousMockState.None, await signedIn.ReleaseAnonymousAsync(anonymous.Clock, finish: false));
    }

    [Fact]
    public async Task AnonymousMock_OutboxRefusesTheAnswers_KeepsTheMock()
    {
        Rig anonymous = new(false, StorageKeys.AnonymousOwner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using (MockPersistence before = Persistence(fake, anonymous.Device))
        {
            await before.StartAsync(anonymous.Session, null);
            PickCorrect(anonymous.Session.Attempts[0]);
            Assert.True(await before.SaveAsync(anonymous.Session));
        }

        using MockPersistence signedIn = Persistence(fake, anonymous.Device);
        fake.QuotaFull = true;

        Assert.Equal(AnonymousMockState.Pending, await signedIn.ReleaseAnonymousAsync(anonymous.Clock, finish: true));
        Assert.NotNull(fake.Raw(StorageKeys.MockProgress(StorageKeys.AnonymousOwner)));
        Assert.Null(fake.Raw(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Handoff_QuotaFull_KeepsKeysAndAnswersInMemory()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using MockPersistence persistence = Persistence(fake, rig.Device, out OutboxStore outbox);
        await persistence.StartAsync(rig.Session, TelegramId);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();
        fake.QuotaFull = true;

        MockHandoff outcome = await persistence.HandOffAsync(rig.Session, TelegramId);

        Assert.False(outcome.Cleared);
        Assert.NotNull(fake.Raw(StorageKeys.MockProgress(Owner)));
        Assert.Equal(1, outbox.UnsavedCount);
    }

    [Fact]
    public async Task SecondWriter_GetsOtherWriterAndTurnsReadOnly()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence starter = Persistence(fake, rig.Device);
        await starter.StartAsync(rig.Session, TelegramId);

        using MockPersistence tabA = Persistence(fake, rig.Device);
        using MockPersistence tabB = Persistence(fake, rig.Device);
        MockSession a = (await LoadAdoptedAsync(tabA, rig.Clock))!;
        MockSession b = (await LoadAdoptedAsync(tabB, rig.Clock))!;
        int changes = 0;
        tabB.Changed += () => changes++;

        a.Attempts[0].Toggle("a");
        Assert.True(await tabA.SaveAsync(a));
        b.Attempts[0].Toggle("b");
        Assert.False(await tabB.SaveAsync(b));

        Assert.True(tabB.IsReadOnly);
        Assert.False(tabA.IsReadOnly);
        Assert.Equal(1, changes);
        int ops = fake.Ops.Count;
        Assert.False(await tabB.SaveAsync(b));
        b.Finish();
        MockHandoff outcome = await tabB.HandOffAsync(b, TelegramId);
        Assert.False(outcome.Cleared);
        Assert.Equal(ops, fake.Ops.Count);

        (MockSession? reloaded, _) = await Persistence(fake, rig.Device).LoadAsync(Owner, rig.Clock);
        Assert.Equal(["a"], reloaded!.Attempts[0].Picked);
    }

    [Fact]
    public async Task Start_AnotherActiveMock_IsNotOverwritten()
    {
        Rig first = new(false, Owner, TestQuestions.Single());
        Rig second = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence tabA = Persistence(fake, first.Device);
        using MockPersistence tabB = Persistence(fake, first.Device);

        Assert.True(await tabA.StartAsync(first.Session, TelegramId));
        Assert.False(await tabB.StartAsync(second.Session, TelegramId));

        (MockSession? active, _) = await Persistence(fake, first.Device).LoadAsync(Owner, first.Clock);
        Assert.Equal(first.Context.SessionId, active!.Context.SessionId);
    }

    [Fact]
    public async Task Start_AfterUnfinishedHandoff_HandsItOverBeforeStarting()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        HookStore store = new(new FakeBrowserStore()) { Intercept = FailAppends };
        using MockPersistence tab = Persistence(store, first.Device);
        await tab.StartAsync(first.Session, TelegramId);
        PickCorrect(first.Session.Attempts[0]);
        first.Session.Finish();
        MockHandoff unfinished = await tab.HandOffAsync(first.Session, TelegramId);
        Assert.False(unfinished.AnswersPersisted);
        store.Intercept = null;

        Assert.True(await tab.StartAsync(second.Session, TelegramId));

        Assert.Equal([first.Session.Attempts[0].ClientAnswerId], Ids(store.Inner.Raw(StorageKeys.UserOutbox(TelegramId)), "answers", "clientAnswerId"));
        Assert.Equal([first.Context.SessionId], Ids(store.Inner.Raw(StorageKeys.PendingSummaries(TelegramId)), "items", "mockSessionId"));
        Assert.Null(store.Inner.Raw(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));
        Assert.Equal(second.Context.SessionId, (await LoadAdoptedAsync(Persistence(store.Inner, first.Device), first.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Start_HandoffStillFailing_KeepsTheOldMockAndRefuses()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        HookStore store = new(new FakeBrowserStore()) { Intercept = FailAppends };
        using MockPersistence tab = Persistence(store, first.Device);
        await tab.StartAsync(first.Session, TelegramId);
        PickCorrect(first.Session.Attempts[0]);
        first.Session.Finish();
        await tab.HandOffAsync(first.Session, TelegramId);
        string? before = store.Inner.Raw(StorageKeys.MockProgress(Owner));

        Assert.False(await tab.StartAsync(second.Session, TelegramId));

        Assert.Equal(before, store.Inner.Raw(StorageKeys.MockProgress(Owner)));
        Assert.NotNull(store.Inner.Raw(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));
        Assert.Null(store.Inner.Raw(StorageKeys.MockQuestions(Owner, second.Context.SessionId)));
        Assert.DoesNotContain(store.Inner.Ops, op => op.Op == "delete");
    }

    [Fact]
    public async Task Start_AfterLoadingAnotherTabsMock_NeverDeletesIt()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence tabA = Persistence(fake, first.Device);
        using MockPersistence tabB = Persistence(fake, first.Device);
        await tabA.StartAsync(first.Session, TelegramId);

        // Tab B loaded the mock but did not adopt it, as when its load timed out and it showed the setup form.
        Assert.NotNull((await tabB.LoadAsync(Owner, first.Clock)).Session);
        Assert.False(await tabB.StartAsync(second.Session, TelegramId));

        Assert.DoesNotContain(fake.Ops, op => op.Op == "delete");
        Assert.Equal(first.Context.SessionId, (await LoadAdoptedAsync(Persistence(fake, first.Device), first.Clock))!.Context.SessionId);
        first.Session.Attempts[0].Toggle("a");
        Assert.True(await tabA.SaveAsync(first.Session));
    }

    [Fact]
    public async Task Start_TabClosedMidHandoff_NextTabFinishesItFirst()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open());
        Rig second = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        CrashingStore crashing = new(fake, "append");
        using (MockPersistence closed = Persistence(crashing, first.Device))
        {
            await closed.StartAsync(first.Session, TelegramId);
            PickCorrect(first.Session.Attempts[0]);
            first.Session.Finish();
            first.Session.SkipAssessment(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => closed.HandOffAsync(first.Session, TelegramId));
        }

        using MockPersistence next = Persistence(fake, first.Device);
        Assert.True(await next.StartAsync(second.Session, TelegramId));

        Assert.Equal([first.Session.Attempts[0].ClientAnswerId], Ids(fake.Raw(StorageKeys.UserOutbox(TelegramId)), "answers", "clientAnswerId"));
        Assert.Equal([first.Context.SessionId], Ids(fake.Raw(StorageKeys.PendingSummaries(TelegramId)), "items", "mockSessionId"));
        Assert.Null(fake.Raw(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));
        Assert.Equal(second.Context.SessionId, (await LoadAdoptedAsync(Persistence(fake, first.Device), first.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Start_CompletionWithoutQuestions_IsHandedOverFromTheDocument()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        HookStore store = new(new FakeBrowserStore()) { Intercept = FailAppends };
        using MockPersistence tab = Persistence(store, first.Device);
        await tab.StartAsync(first.Session, TelegramId);
        PickCorrect(first.Session.Attempts[0]);
        first.Session.Finish();
        await tab.HandOffAsync(first.Session, TelegramId);
        store.Intercept = null;
        await store.Inner.MutateAsync(StoreOp.Delete(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));

        using MockPersistence other = Persistence(store.Inner, first.Device);
        Assert.True(await other.StartAsync(second.Session, TelegramId));

        Assert.Single(Ids(store.Inner.Raw(StorageKeys.UserOutbox(TelegramId)), "answers", "clientAnswerId"));
        Assert.Equal([first.Context.SessionId], Ids(store.Inner.Raw(StorageKeys.PendingSummaries(TelegramId)), "items", "mockSessionId"));
        Assert.Equal(second.Context.SessionId, (await LoadAdoptedAsync(Persistence(store.Inner, first.Device), first.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Start_UnfinishedWithoutQuestions_IsReplaced()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence tab = Persistence(fake, first.Device);
        await tab.StartAsync(first.Session, TelegramId);
        await fake.MutateAsync(StoreOp.Delete(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));

        using MockPersistence other = Persistence(fake, first.Device);
        Assert.True(await other.StartAsync(second.Session, TelegramId));

        Assert.Equal(second.Context.SessionId, (await LoadAdoptedAsync(Persistence(fake, first.Device), first.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Start_TwoTabsAtOnce_LoserRemovesItsQuestionsAndRefuses()
    {
        Rig winner = new(false, Owner, TestQuestions.Single());
        Rig loser = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence tabB = Persistence(fake, winner.Device);
        HookStore store = new(fake);
        store.Intercept = async op =>
        {
            if (op.Op == "putIfRevision")
            {
                // Tab B starts between tab A's check and tab A's first progress write.
                store.Intercept = null;
                await tabB.StartAsync(winner.Session, TelegramId);
            }

            return null;
        };
        using MockPersistence tabA = Persistence(store, winner.Device);

        Assert.False(await tabA.StartAsync(loser.Session, TelegramId));

        Assert.False(tabA.IsReadOnly);
        Assert.Null(fake.Raw(StorageKeys.MockQuestions(Owner, loser.Context.SessionId)));
        Assert.NotNull(fake.Raw(StorageKeys.MockQuestions(Owner, winner.Context.SessionId)));
        Assert.Equal(winner.Context.SessionId, (await LoadAdoptedAsync(Persistence(fake, winner.Device), winner.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Handoff_ReadOnlyAfterFirstSave_StopsBeforeTheOutbox()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        FakeBrowserStore fake = new();
        using MockPersistence starter = Persistence(fake, rig.Device);
        await starter.StartAsync(rig.Session, TelegramId);
        using MockPersistence tabA = Persistence(fake, rig.Device);
        using MockPersistence tabB = Persistence(fake, rig.Device);
        MockSession a = (await LoadAdoptedAsync(tabA, rig.Clock))!;
        MockSession b = (await LoadAdoptedAsync(tabB, rig.Clock))!;
        a.Attempts[0].Toggle("b");
        await tabA.SaveAsync(a);
        PickCorrect(b.Attempts[0]);
        b.Finish();

        MockHandoff outcome = await tabB.HandOffAsync(b, TelegramId);

        Assert.True(tabB.IsReadOnly);
        Assert.Equal(new MockHandoff(false, false, false), outcome);
        Assert.DoesNotContain(fake.Ops, op => op.Op is "append" or "summaryAdd" or "delete");
    }

    [Fact]
    public async Task Handoff_AnswersNotPersisted_DoesNotQueueTheSummary()
    {
        Rig rig = new(false, Owner, TestQuestions.Single(correct: "a"));
        HookStore store = new(new FakeBrowserStore()) { Intercept = FailAppends };
        using MockPersistence persistence = Persistence(store, rig.Device);
        await persistence.StartAsync(rig.Session, TelegramId);
        PickCorrect(rig.Session.Attempts[0]);
        rig.Session.Finish();

        MockHandoff outcome = await persistence.HandOffAsync(rig.Session, TelegramId);

        Assert.Equal(new MockHandoff(false, false, false), outcome);
        Assert.DoesNotContain(store.Inner.Ops, op => op.Op == "summaryAdd");
    }

    [Fact]
    public void Finish_AfterDeadline_FinishedAtIsTheDeadline()
    {
        Rig rig = new(true, Owner, TestQuestions.Single(), TestQuestions.Single());
        rig.Device.Advance(TimeSpan.FromDays(2));

        Assert.True(rig.Session.Finish());

        Assert.Equal(rig.Session.Deadline, rig.Session.FinishedAt);
    }

    [Fact]
    public async Task TimerExpiryDuringSave_LastWriteIsTheFinishedState()
    {
        Rig rig = new(true, Owner, TestQuestions.Single(correct: "a"), TestQuestions.Open());
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HookStore store = new(new FakeBrowserStore());
        using MockPersistence persistence = Persistence(store, rig.Device);
        await persistence.StartAsync(rig.Session, TelegramId);
        store.Intercept = async op =>
        {
            if (op.Op == "putIfRevision")
            {
                await gate.Task;
            }

            return null;
        };

        PickCorrect(rig.Session.Attempts[0]);
        Task<bool> pick = persistence.SaveAsync(rig.Session);
        Assert.True(rig.Session.Finish());
        Task<bool> finish = persistence.SaveAsync(rig.Session);
        gate.SetResult();

        bool[] saved = await Task.WhenAll(pick, finish);
        Assert.All(saved, Assert.True);
        using JsonDocument stored = JsonDocument.Parse(store.Inner.Raw(StorageKeys.MockProgress(Owner))!);
        Assert.Equal("assessment", stored.RootElement.GetProperty("phase").GetString());
        Assert.Equal(3, stored.RootElement.GetProperty("revision").GetInt64());
    }

    // A finished mock whose answers did not reach the outbox: its completion is stored, its hand-over is not durable.
    private static async Task<(HookStore Store, MockPersistence Tab)> UndeliveredMockAsync(Rig first)
    {
        HookStore store = new(new FakeBrowserStore()) { Intercept = FailAppends };
        MockPersistence tab = Persistence(store, first.Device);
        await tab.StartAsync(first.Session, TelegramId);
        PickCorrect(first.Session.Attempts[0]);
        first.Session.Finish();
        await tab.HandOffAsync(first.Session, TelegramId);
        store.Intercept = null;
        return (store, tab);
    }

    [Fact]
    public async Task Start_ReleaseSlowerThanTimeout_NeverStartsTheNewMock()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        (HookStore store, MockPersistence tab) = await UndeliveredMockAsync(first);
        using MockPersistence disposeTab = tab;
        string? before = store.Inner.Raw(StorageKeys.MockProgress(Owner));
        TaskCompletionSource<StoreResult?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Intercept = op => op.Op == "append" ? new ValueTask<StoreResult?>(gate.Task) : ValueTask.FromResult<StoreResult?>(null);

        Task<bool> start = tab.StartAsync(second.Session, TelegramId);
        first.Device.Advance(MockPersistence.WriteTimeout + TimeSpan.FromSeconds(1));
        for (int i = 0; i < 200 && !tab.IsSlow; i++)
        {
            await Task.Delay(5, Ct);
        }

        // The release outlives the bound: Start keeps waiting (the island keeps Start blocked) and says it is slow.
        Assert.True(tab.IsSlow);
        Assert.False(start.IsCompleted);

        gate.SetResult(StoreResult.Failed(StoreErrors.Quota));
        Assert.False(await start);

        // Refused: the new mock is detached, so it never writes over the stored one.
        second.Session.Attempts[0].SetNotes("x");
        Assert.False(await tab.SaveAsync(second.Session));
        Assert.Equal(before, store.Inner.Raw(StorageKeys.MockProgress(Owner)));
        Assert.Null(store.Inner.Raw(StorageKeys.MockQuestions(Owner, second.Context.SessionId)));
        for (int i = 0; i < 200 && tab.IsSlow; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.False(tab.IsSlow);
    }

    [Fact]
    public async Task StaleTab_SavingAnotherSessionAtMatchingRevision_TurnsReadOnly()
    {
        Rig live = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig stale = new(false, Owner, TestQuestions.Open());
        FakeBrowserStore fake = new();
        using MockPersistence tabA = Persistence(fake, live.Device);
        using MockPersistence tabB = Persistence(fake, live.Device);
        await tabA.StartAsync(live.Session, TelegramId);
        (_, MockProgress? progress) = await tabB.LoadAsync(Owner, live.Clock);
        tabB.Adopt(progress!);

        Assert.False(await tabB.SaveAsync(stale.Session));

        Assert.True(tabB.IsReadOnly);
        using JsonDocument stored = JsonDocument.Parse(fake.Raw(StorageKeys.MockProgress(Owner))!);
        Assert.Equal(live.Context.SessionId, stored.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, stored.RootElement.GetProperty("revision").GetInt64());
        live.Session.Attempts[0].Toggle("a");
        Assert.True(await tabA.SaveAsync(live.Session));
    }

    [Fact]
    public async Task Start_ReleaseFlagSaveFails_StillHandsOverWithoutBumpingTheRevision()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        (HookStore store, MockPersistence tab) = await UndeliveredMockAsync(first);
        using MockPersistence disposeTab = tab;
        bool failed = false;
        store.Intercept = op =>
        {
            if (op.Op == "putIfRevision" && !failed)
            {
                failed = true;
                return ValueTask.FromResult<StoreResult?>(StoreResult.Failed(StoreErrors.Quota));
            }

            return ValueTask.FromResult<StoreResult?>(null);
        };

        Assert.True(await tab.StartAsync(second.Session, TelegramId));

        Assert.True(failed);
        Assert.Equal([first.Session.Attempts[0].ClientAnswerId], Ids(store.Inner.Raw(StorageKeys.UserOutbox(TelegramId)), "answers", "clientAnswerId"));
        Assert.Equal([first.Context.SessionId], Ids(store.Inner.Raw(StorageKeys.PendingSummaries(TelegramId)), "items", "mockSessionId"));
        Assert.Equal(second.Context.SessionId, (await LoadAdoptedAsync(Persistence(store.Inner, first.Device), first.Clock))!.Context.SessionId);
    }

    [Fact]
    public async Task Start_ReleaseClearFails_Refuses()
    {
        Rig first = new(false, Owner, TestQuestions.Single(correct: "a"));
        Rig second = new(false, Owner, TestQuestions.Open());
        (HookStore store, MockPersistence tab) = await UndeliveredMockAsync(first);
        using MockPersistence disposeTab = tab;
        store.Intercept = op => ValueTask.FromResult<StoreResult?>(
            op.Op == "delete" && op.Key == StorageKeys.MockProgress(Owner) ? StoreResult.Failed(StoreErrors.WriteFailed) : null);

        Assert.False(await tab.StartAsync(second.Session, TelegramId));

        Assert.NotNull(store.Inner.Raw(StorageKeys.MockQuestions(Owner, first.Context.SessionId)));
        Assert.Null(store.Inner.Raw(StorageKeys.MockQuestions(Owner, second.Context.SessionId)));
        Assert.Equal(first.Context.SessionId, (await LoadAdoptedAsync(Persistence(store.Inner, first.Device), first.Clock))!.Context.SessionId);
    }

    private static ValueTask<StoreResult?> FailAppends(StoreOp op) =>
        ValueTask.FromResult(op.Op == "append" ? StoreResult.Failed(StoreErrors.Quota) : null);

    private static async Task<MockSession?> LoadAdoptedAsync(MockPersistence persistence, ServerClock clock)
    {
        (MockSession? session, MockProgress? progress) = await persistence.LoadAsync(Owner, clock);
        if (session is not null)
        {
            persistence.Adopt(progress!);
        }

        return session;
    }

    private static MockPersistence Persistence(IBrowserStore store, TimeProvider time) => Persistence(store, time, out _);

    private static MockPersistence Persistence(IBrowserStore store, TimeProvider time, out OutboxStore outbox) =>
        Persistence(store, time, out outbox, out _);

    private static MockPersistence Persistence(IBrowserStore store, TimeProvider time, out OutboxStore outbox, out SummaryStore summaries)
    {
        outbox = new OutboxStore(store);
        summaries = new SummaryStore(store);
        return new MockPersistence(new ActiveMockStore(store), outbox, summaries, time);
    }

    private static string[] Ids(string? raw, string list, string id)
    {
        if (raw is null)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        return [.. document.RootElement.GetProperty(list).EnumerateArray().Select(e => e.GetProperty(id).GetString()!)];
    }

    /// <summary>Applies ops until the crash point, then throws for every later op, as if the tab had been closed.</summary>
    private sealed class CrashingStore(FakeBrowserStore inner, string crashPoint) : IBrowserStore
    {
        public bool Crashed { get; private set; }

        public ValueTask<string?> ReadAsync(string key) => Crashed ? throw new InvalidOperationException("closed") : inner.ReadAsync(key);

        public async ValueTask<StoreResult> MutateAsync(StoreOp op)
        {
            if (Crashed || (crashPoint == "delete" && op.Op == "delete"))
            {
                Crashed = true;
                throw new InvalidOperationException("closed");
            }

            StoreResult result = await inner.MutateAsync(op);
            bool completionSaved = crashPoint == "complete"
                && op.Op == "putIfRevision"
                && op.Value is { } value
                && value.TryGetProperty("completion", out JsonElement completion)
                && completion.ValueKind == JsonValueKind.Object;
            Crashed = completionSaved || string.Equals(op.Op, crashPoint, StringComparison.Ordinal);
            return result;
        }
    }
}
