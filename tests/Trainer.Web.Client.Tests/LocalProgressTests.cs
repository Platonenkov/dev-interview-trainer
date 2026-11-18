using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class LocalProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Sm2ReviewScheduler Scheduler = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AnswerDto Answer(QuestionDto question, string grade, DateTimeOffset at, string? id = null) =>
        StaticSiteFixture.Answer(question, grade, at, id);

    private static QuestionId IdOf(QuestionDto question) => new(question.Id);

    private static void AssertSameStates(LocalProgress expected, LocalProgress actual)
    {
        Assert.Equal(expected.States.Count, actual.States.Count);
        foreach ((QuestionId id, ReviewState state) in expected.States)
        {
            Assert.Equal(state, actual.States[id]);
        }
    }

    [Fact]
    public void Replay_AppliesAnswersInServerOrder()
    {
        QuestionDto q = TestQuestions.Open();
        AnswerDto knewLate = Answer(q, "knew", Now.AddDays(-1));
        AnswerDto partialMid = Answer(q, "partial", Now.AddDays(-2));
        AnswerDto knewFirst = Answer(q, "knew", Now.AddDays(-5));

        LocalProgress progress = LocalProgress.Replay([knewLate, partialMid, knewFirst], Scheduler, Now);

        ReviewState expected = Scheduler.Apply(null, Grade.Knew, Now.AddDays(-5));
        expected = Scheduler.Apply(expected, Grade.Partial, Now.AddDays(-2));
        expected = Scheduler.Apply(expected, Grade.Knew, Now.AddDays(-1));
        Assert.Equal(expected, progress.States[IdOf(q)]);
    }

    [Fact]
    public void Replay_EqualTimes_AreOrderedByClientAnswerIdOrdinal()
    {
        QuestionDto q = TestQuestions.Open();
        DateTimeOffset at = Now.AddDays(-1);
        AnswerDto second = Answer(q, "didnt-know", at, "01JD0000000000000000000002");
        AnswerDto first = Answer(q, "knew", at, "01JD0000000000000000000001");

        LocalProgress progress = LocalProgress.Replay([second, first], Scheduler, Now);

        // The scheduler ignores an answer that is not newer than the last applied one, so the lower id wins the tie.
        Assert.Equal(Grade.Knew, progress.States[IdOf(q)].LastGrade);
    }

    [Fact]
    public void Replay_LateBackdatedAnswer_TakesItsChronologicalPlace()
    {
        QuestionDto q = TestQuestions.Open();
        AnswerDto day1 = Answer(q, "knew", Now.AddDays(-10));
        AnswerDto day5 = Answer(q, "knew", Now.AddDays(-6));
        AnswerDto backdated = Answer(q, "didnt-know", Now.AddDays(-8));

        // The backdated answer is the last one written to the log.
        LocalProgress withLate = LocalProgress.Replay([day1, day5, backdated], Scheduler, Now);
        LocalProgress sortedFromScratch = LocalProgress.Replay([day1, backdated, day5], Scheduler, Now);

        AssertSameStates(sortedFromScratch, withLate);
        // A fold in arrival order would drop the backdated answer as stale and end elsewhere.
        ReviewState arrival = Scheduler.Apply(Scheduler.Apply(Scheduler.Apply(null, Grade.Knew, day1.AnsweredAt!.Value), Grade.Knew, day5.AnsweredAt!.Value), Grade.DidntKnow, backdated.AnsweredAt!.Value);
        Assert.NotEqual(arrival, withLate.States[IdOf(q)]);
    }

    [Fact]
    public void Replay_KeepsStatesOfOtherStacksAndLanguages()
    {
        QuestionDto here = TestQuestions.Open();
        QuestionDto elsewhere = TestQuestions.Open();
        LocalProgress progress = LocalProgress.Replay(
            [Answer(here, "knew", Now.AddDays(-3)), Answer(elsewhere, "partial", Now.AddDays(-3))],
            Scheduler,
            Now);

        Assert.Equal(2, progress.States.Count);
        IReadOnlyDictionary<QuestionId, DateTimeOffset> current = progress.DueAtById(new HashSet<QuestionId> { IdOf(here) });
        Assert.Equal(IdOf(here), Assert.Single(current).Key);

        // The state of the question missing from this index is still there when an index that lists it is used.
        IReadOnlyDictionary<QuestionId, DateTimeOffset> other = progress.DueAtById(new HashSet<QuestionId> { IdOf(here), IdOf(elsewhere) });
        Assert.Equal(2, other.Count);
        Assert.Equal(2, progress.DueStates().Count());
    }

    [Fact]
    public void Replay_SameLogSameStatesAcrossDaysAndReloads()
    {
        QuestionDto q = TestQuestions.Open();
        AnswerDto future = Answer(q, "knew", Now.AddDays(2));
        AnswerDto past = Answer(q, "partial", Now.AddDays(-4));

        LocalProgress today = LocalProgress.Replay([future, past], Scheduler, Now);
        LocalProgress later = LocalProgress.Replay([past, future], Scheduler, Now.AddDays(30));
        LocalProgress earlier = LocalProgress.Replay([future, past], Scheduler, Now.AddDays(-30));

        AssertSameStates(today, later);
        AssertSameStates(today, earlier);
        Assert.Equal(Now.AddDays(2), later.States[IdOf(q)].LastAnsweredAt);
    }

    [Fact]
    public void Replay_NeverReClampsAnsweredAt()
    {
        QuestionDto q = TestQuestions.Open();
        DateTimeOffset ahead = Now.AddDays(2);

        LocalProgress progress = LocalProgress.Replay([Answer(q, "knew", ahead)], Scheduler, Now);

        Assert.Equal(ahead, progress.States[IdOf(q)].LastAnsweredAt);
        Assert.Equal(ahead.AddDays(1), progress.States[IdOf(q)].DueAt);
    }

    [Fact]
    public void Replay_SkipsAnswersOutsideTheSanityWindow_InsteadOfFailing()
    {
        QuestionDto q = TestQuestions.Open();
        QuestionDto other = TestQuestions.Single();
        AnswerDto good = Answer(q, "knew", Now.AddDays(-1));
        AnswerDto[] extreme =
        [
            Answer(other, "knew", new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero)),
            Answer(other, "knew", DateTimeOffset.MaxValue),
            Answer(other, "knew", DateTimeOffset.MinValue),
            Answer(other, "knew", new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero)),
            Answer(other, "knew", new DateTimeOffset(2100, 1, 1, 0, 1, 0, TimeSpan.Zero)),
        ];

        LocalProgress progress = LocalProgress.Replay([.. extreme, good], Scheduler, Now);

        Assert.Equal(1, progress.TotalAnswered);
        Assert.Equal(Scheduler.Apply(null, Grade.Knew, Now.AddDays(-1)), progress.States[IdOf(q)]);
        Assert.DoesNotContain(IdOf(other), progress.States.Keys);
        Assert.Equal(1, progress.StreakDays(Now, TimeSpan.Zero));
    }

    [Fact]
    public void Replay_KeepsAnswersAtTheEdgesOfTheSanityWindow()
    {
        QuestionDto q = TestQuestions.Open();
        QuestionDto other = TestQuestions.Single();
        DateTimeOffset earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset latest = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

        LocalProgress progress = LocalProgress.Replay([Answer(q, "knew", earliest), Answer(other, "knew", latest)], Scheduler, Now);

        Assert.Equal(2, progress.TotalAnswered);
    }

    [Fact]
    public void Replay_EqualsApplyingTheSchedulerDirectly()
    {
        QuestionDto a = TestQuestions.Open();
        QuestionDto b = TestQuestions.Open();
        Random random = new(11);
        string[] grades = ["knew", "partial", "didnt-know"];
        List<AnswerDto> log = [];
        for (int i = 0; i < 60; i++)
        {
            log.Add(Answer(i % 3 == 0 ? b : a, grades[random.Next(3)], Now.AddHours(-random.Next(1, 24 * 40))));
        }

        LocalProgress progress = LocalProgress.Replay(log, Scheduler, Now);

        foreach (QuestionDto question in new[] { a, b })
        {
            ReviewState? state = null;
            foreach (AnswerDto answer in log.Where(x => x.QuestionId == question.Id).OrderBy(x => x.AnsweredAt).ThenBy(x => x.ClientAnswerId, StringComparer.Ordinal))
            {
                Assert.True(GradeKeys.TryParse(answer.Grade, out Grade grade));
                state = Scheduler.Apply(state, grade, answer.AnsweredAt!.Value);
            }

            Assert.Equal(state, progress.States[IdOf(question)]);
        }
    }

    [Fact]
    public void Replay_SkipsEntriesThatAreNotAnswers()
    {
        QuestionDto q = TestQuestions.Open();
        AnswerDto good = Answer(q, "knew", Now.AddDays(-1));
        AnswerDto noTime = good with { ClientAnswerId = "01JD0000000000000000000091", AnsweredAt = null };
        AnswerDto badGrade = good with { ClientAnswerId = "01JD0000000000000000000092", Grade = "great" };
        AnswerDto badQuestion = good with { ClientAnswerId = "01JD0000000000000000000093", QuestionId = "not-an-id" };

        LocalProgress progress = LocalProgress.Replay([noTime, badGrade, badQuestion, good], Scheduler, Now);

        Assert.Single(progress.States);
        Assert.Equal(1, progress.TotalAnswered);
    }

    [Fact]
    public void Streak_CountsDaysInTheDeviceOffset()
    {
        QuestionDto q = TestQuestions.Open();
        TimeSpan plusThree = TimeSpan.FromHours(3);
        // 23:30 and 00:30 local (+03:00) are 20:30 and 21:30 UTC of the same UTC day.
        DateTimeOffset lateEvening = new(2026, 11, 10, 20, 30, 0, TimeSpan.Zero);
        DateTimeOffset justAfterMidnight = new(2026, 11, 10, 21, 30, 0, TimeSpan.Zero);
        LocalProgress progress = LocalProgress.Replay([Answer(q, "knew", lateEvening), Answer(q, "partial", justAfterMidnight)], Scheduler, Now);
        DateTimeOffset now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(2, progress.StreakDays(now, plusThree));
        Assert.Equal(1, progress.StreakDays(now, TimeSpan.Zero));
    }

    [Fact]
    public void Streak_IsAliveTodayOrYesterdayAndBreaksOnAGap()
    {
        QuestionDto q = TestQuestions.Open();
        LocalProgress progress = LocalProgress.Replay(
            [Answer(q, "knew", Now.AddDays(-1)), Answer(q, "knew", Now.AddDays(-2)), Answer(q, "knew", Now.AddDays(-4))],
            Scheduler,
            Now);

        Assert.Equal(2, progress.StreakDays(Now, TimeSpan.Zero));
        Assert.Equal(0, progress.StreakDays(Now.AddDays(3), TimeSpan.Zero));
        Assert.Equal(0, LocalProgress.Empty.StreakDays(Now, TimeSpan.Zero));
    }

    [Fact]
    public async Task Cache_ReplaysTheAnonymousLogOnce_AndOnlyReadsItOnce()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(q, "knew", Now.AddDays(-1)));
        tab.Hook.Reads.Clear();

        LocalProgress first = await tab.Progress.GetAsync(Ct);
        LocalProgress second = await tab.Progress.GetAsync(Ct);

        Assert.Single(first.States);
        Assert.Same(first, second);
        Assert.Single(tab.Hook.Reads, StorageKeys.AnonymousOutbox);
    }

    [Fact]
    public async Task Cache_OwnWriteDropsTheMemo()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab tab = site.NewTab();
        Assert.Empty((await tab.Progress.GetAsync(Ct)).States);

        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(q, "knew", Now.AddDays(-1)));

        Assert.Single((await tab.Progress.GetAsync(Ct)).States);
    }

    [Fact]
    public async Task Recompute_AfterAnotherTabWrites()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab mine = site.NewTab();
        StaticSiteFixture.Tab other = site.NewTab();
        Assert.Empty((await mine.Progress.GetAsync(Ct)).States);
        Assert.Equal(1, mine.Hook.WatcherCount(StorageKeys.AnonymousOutbox));

        await other.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(q, "knew", Now.AddDays(-1)));
        // Nothing tells this tab yet, so it keeps what it computed.
        Assert.Empty((await mine.Progress.GetAsync(Ct)).States);

        mine.Hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox);

        Assert.Single((await mine.Progress.GetAsync(Ct)).States);
    }

    [Fact]
    public async Task Cache_WatchesBeforeItReads_AndStopsWatchingWhenTheOutboxIsDisposed()
    {
        StaticSiteFixture site = new();
        StaticSiteFixture.Tab tab = site.NewTab();
        int watchersAtFirstRead = -1;
        tab.Hook.ReadGate = key =>
        {
            watchersAtFirstRead = tab.Hook.WatcherCount(key);
            return Task.CompletedTask;
        };

        await tab.Progress.GetAsync(Ct);
        Assert.Equal(1, watchersAtFirstRead);

        await tab.Outbox.DisposeAsync();
        Assert.Equal(0, tab.Hook.WatcherCount(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Cache_AFailedReadIsNotMemoised()
    {
        StaticSiteFixture site = new();
        StaticSiteFixture.Tab tab = site.NewTab();
        tab.Hook.ReadGate = _ => throw new InvalidOperationException("storage blew up");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tab.Progress.GetAsync(Ct));

        tab.Hook.ReadGate = null;

        Assert.Empty((await tab.Progress.GetAsync(Ct)).States);
    }

    [Fact]
    public async Task Cache_ACancelledCallerDoesNotCancelTheSharedReplay()
    {
        StaticSiteFixture site = new();
        StaticSiteFixture.Tab tab = site.NewTab();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Hook.ReadGate = _ => release.Task;
        using CancellationTokenSource cancel = new();

        Task<LocalProgress> cancelled = tab.Progress.GetAsync(cancel.Token).AsTask();
        Task<LocalProgress> kept = tab.Progress.GetAsync(Ct).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        Assert.Empty((await kept).States);
    }

    [Fact]
    public async Task OverlappingWritersFromTwoTabs_NeverLoseAnAnswer()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab a = site.NewTab();
        StaticSiteFixture.Tab b = site.NewTab();
        TaskCompletionSource releaseA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Tab A's append is in flight (it has not reached storage) while tab B's append completes.
        a.Hook.Intercept = async _ =>
        {
            await releaseA.Task;
            return null;
        };
        AnswerDto fromA = Answer(q, "knew", Now.AddDays(-3));
        AnswerDto fromB = Answer(q, "partial", Now.AddDays(-2));

        Task writeA = a.Outbox.AddAsync(StorageKeys.AnonymousOutbox, fromA).AsTask();
        await b.Outbox.AddAsync(StorageKeys.AnonymousOutbox, fromB);
        releaseA.SetResult();
        await writeA;

        StaticSiteFixture.Tab reader = site.NewTab();
        LocalProgress replayed = await reader.Progress.GetAsync(Ct);
        Assert.Equal(2, replayed.TotalAnswered);
        Assert.Equal(LocalProgress.Replay([fromA, fromB], Scheduler, Now).States[IdOf(q)], replayed.States[IdOf(q)]);

        // Each writer sees the other's answer once it hears of the write.
        a.Hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox);
        b.Hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox);
        Assert.Equal(2, (await a.Progress.GetAsync(Ct)).TotalAnswered);
        Assert.Equal(2, (await b.Progress.GetAsync(Ct)).TotalAnswered);
    }

    [Fact]
    public async Task ManyOverlappingWritesFromTwoTabs_KeepEveryAnswer()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab a = site.NewTab();
        StaticSiteFixture.Tab b = site.NewTab();
        for (int i = 0; i < 30; i++)
        {
            // Each round, tab A's append is in flight while tab B's runs to the end, then A's lands on top of B's.
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Hook.Intercept = async _ =>
            {
                await release.Task;
                return null;
            };
            Task fromA = a.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(q, "knew", Now.AddMinutes(-2 * i - 1))).AsTask();
            await b.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(q, "knew", Now.AddMinutes(-2 * i - 2)));
            release.SetResult();
            await fromA;
        }

        LocalProgress replayed = await site.NewTab().Progress.GetAsync(Ct);
        Assert.Equal(60, replayed.TotalAnswered);
    }

    [Fact]
    public async Task Replay_10000Answers_AppliesTheSchedulerOncePerAnswerAndReadsTheLogOnce()
    {
        StaticSiteFixture site = new();
        QuestionDto[] questions = [.. Enumerable.Range(0, 100).Select(_ => TestQuestions.Open())];
        StaticSiteFixture.Tab writer = site.NewTab();
        List<AnswerDto> log = [];
        for (int i = 0; i < 10_000; i++)
        {
            log.Add(Answer(questions[i % questions.Length], i % 5 == 0 ? "didnt-know" : "knew", Now.AddMinutes(-10_000 + i)));
        }

        Assert.True(await writer.Outbox.AddAllAsync(StorageKeys.AnonymousOutbox, log));

        CountingScheduler counting = new();
        StaticSiteFixture.Tab fresh = site.NewTab(scheduler: counting);
        LocalProgress progress = await fresh.Progress.GetAsync(Ct);

        Assert.Equal(10_000, counting.Applied);
        Assert.Equal(10_000, progress.TotalAnswered);
        Assert.Equal(100, progress.States.Count);
        Assert.Single(fresh.Hook.Reads, StorageKeys.AnonymousOutbox);
    }

    [Fact]
    public async Task Cache_IsNotDroppedByItsOwnFirstLoad_EvenWhenTheReadChangesTheStorageStatus()
    {
        StaticSiteFixture site = new();
        // A newer app's document: reading it moves the outbox status to NewerVersion, which raises Changed.
        site.Store.SetRaw(StorageKeys.AnonymousOutbox, "{\"version\":2,\"answers\":[]}");
        StaticSiteFixture.Tab tab = site.NewTab();

        LocalProgress first = await tab.Progress.GetAsync(Ct);
        LocalProgress second = await tab.Progress.GetAsync(Ct);

        Assert.Equal(StorageStatus.NewerVersion, tab.Outbox.Status);
        Assert.Same(first, second);
        Assert.Single(tab.Hook.Reads, StorageKeys.AnonymousOutbox);
    }

    [Fact]
    public async Task Cache_IsNotDroppedByAWriteToAnotherKey()
    {
        StaticSiteFixture site = new();
        QuestionDto q = TestQuestions.Open();
        StaticSiteFixture.Tab tab = site.NewTab();
        LocalProgress first = await tab.Progress.GetAsync(Ct);

        await tab.Outbox.AddAsync(StorageKeys.UserOutbox(42), Answer(q, "knew", Now.AddDays(-1)));
        // Reading another owner's outbox (a status change) must not drop it either.
        await tab.Outbox.ReadAsync(StorageKeys.UserOutbox(42));

        Assert.Same(first, await tab.Progress.GetAsync(Ct));
    }
}
