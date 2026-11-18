using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// The stored copy of the replayed progress (<see cref="ProgressCheckpoint"/>) and the cache that uses it: a checkpoint must
/// give exactly the progress a replay of the log gives, and is trusted only for the log state it was written for.
/// </summary>
public sealed class LocalProgressCheckpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Sm2ReviewScheduler Scheduler = new();

    // One fixed trio: the factory gives every call a new question id, and the logs below must name the same questions.
    private static readonly QuestionDto OpenQ = TestQuestions.Open();
    private static readonly QuestionDto SingleQ = TestQuestions.Single();
    private static readonly QuestionDto MultiQ = TestQuestions.Multi();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AnswerDto Answer(QuestionDto question, string grade, DateTimeOffset at, string? id = null) =>
        StaticSiteFixture.Answer(question, grade, at, id);

    private static ProgressCheckpoint RoundTrip(ProgressCheckpoint checkpoint) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(checkpoint, ProgressCheckpointJsonContext.Default.ProgressCheckpoint), ProgressCheckpointJsonContext.Default.ProgressCheckpoint)!;

    private static void AssertSameProgress(LocalProgress expected, LocalProgress actual)
    {
        Assert.Equal(expected.TotalAnswered, actual.TotalAnswered);
        Assert.Equal(expected.States.Count, actual.States.Count);
        foreach ((QuestionId id, ReviewState state) in expected.States)
        {
            Assert.Equal(state, actual.States[id]);
            Assert.Equal(state.DueAt.UtcTicks, actual.States[id].DueAt.UtcTicks);
            Assert.Equal(state.LastAnsweredAt.UtcTicks, actual.States[id].LastAnsweredAt.UtcTicks);
        }

        // The streak reads the distinct answer minutes, so it is compared at offsets that put the same instants on other days.
        foreach (TimeSpan offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(3), TimeSpan.FromHours(-11), new TimeSpan(5, 45, 0), new TimeSpan(-3, -30, 0), new TimeSpan(0, 7, 0) })
        {
            foreach (int days in new[] { 0, 1, 2, 3, 30 })
            {
                DateTimeOffset when = Now.AddDays(days).AddHours(2);
                Assert.Equal(expected.StreakDays(when, offset), actual.StreakDays(when, offset));
            }
        }
    }

    private static List<AnswerDto> MixedLog(out QuestionDto[] questions)
    {
        questions = [OpenQ, SingleQ, MultiQ];
        List<AnswerDto> log = [];
        // Several answers per question over many days (with same-minute repeats and a day without answers), in every grade.
        string[] grades = ["knew", "partial", "didnt-know", "knew", "knew", "partial"];
        for (int day = 0; day < 20; day++)
        {
            if (day is 7 or 8)
            {
                continue;
            }

            for (int i = 0; i < questions.Length; i++)
            {
                DateTimeOffset at = Now.AddDays(-30 + day).AddMinutes(i * 17);
                log.Add(Answer(questions[i], grades[(day + i) % grades.Length], at));
                if (day % 5 == 0)
                {
                    log.Add(Answer(questions[i], "knew", at.AddSeconds(20)));
                }
            }
        }

        return log;
    }

    [Fact]
    public void Checkpoint_GivesExactlyWhatAReplayGives()
    {
        List<AnswerDto> log = MixedLog(out _);
        LocalProgress replayed = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("63:abc");

        LocalProgress restored = LocalProgress.FromCheckpoint(RoundTrip(replayed.ToCheckpoint()!), "63:abc")!;

        Assert.NotNull(restored);
        Assert.Equal("63:abc", restored.Fingerprint);
        AssertSameProgress(replayed, restored);
    }

    [Fact]
    public void Checkpoint_KeepsTimesBeforeTheEpochAndVeryLongGaps()
    {
        // The minutes are stored as gaps; the first one and a gap of decades must survive the round trip.
        DateTimeOffset[] times =
        [
            new(1969, 12, 31, 23, 59, 30, TimeSpan.Zero),
            new(1970, 1, 1, 0, 0, 10, TimeSpan.Zero),
            new(1970, 1, 2, 0, 5, 0, TimeSpan.Zero),
            new(2100, 1, 1, 12, 0, 0, TimeSpan.Zero),
        ];
        QuestionDto[] questions = [OpenQ, SingleQ];
        List<AnswerDto> log = [.. times.Select((at, i) => Answer(questions[i % 2], "knew", at))];
        LocalProgress replayed = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("4:abc");

        LocalProgress restored = LocalProgress.FromCheckpoint(RoundTrip(replayed.ToCheckpoint()!), "4:abc")!;

        Assert.Equal(replayed.TotalAnswered, restored.TotalAnswered);
        foreach (TimeSpan offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(5), TimeSpan.FromHours(-6) })
        {
            foreach (DateTimeOffset when in new DateTimeOffset[] { new(1970, 1, 2, 10, 0, 0, TimeSpan.Zero), new(1970, 1, 3, 10, 0, 0, TimeSpan.Zero), new(2100, 1, 1, 13, 0, 0, TimeSpan.Zero) })
            {
                Assert.Equal(replayed.StreakDays(when, offset), restored.StreakDays(when, offset));
            }
        }
    }

    [Fact]
    public void Checkpoint_OfAnEmptyLog_IsEmpty()
    {
        LocalProgress restored = LocalProgress.FromCheckpoint(RoundTrip(LocalProgress.Empty.WithFingerprint("0:0").ToCheckpoint()!), "0:0")!;

        Assert.Empty(restored.States);
        Assert.Equal(0, restored.TotalAnswered);
        Assert.Equal(0, restored.StreakDays(Now, TimeSpan.Zero));
    }

    [Fact]
    public void Checkpoint_IsNotMadeWithoutAKnownLogState() =>
        Assert.Null(LocalProgress.Replay(MixedLog(out _), Scheduler, Now).ToCheckpoint());

    public static TheoryData<string> DamagedCheckpoints() => new()
    {
        "another version",
        "another algorithm",
        "another log state",
        "odd minutes",
        "not hex",
        "minutes not ascending",
        "truncated minute",
        "oversized minute",
        "unknown grade",
        "malformed question id",
        "negative count",
    };

    [Theory]
    [MemberData(nameof(DamagedCheckpoints))]
    public void Checkpoint_IsIgnored_WhenItIsNotExactlyOneThisBuildWroteForThisLog(string damage)
    {
        LocalProgress replayed = LocalProgress.Replay(MixedLog(out _), Scheduler, Now).WithFingerprint("63:abc");
        ProgressCheckpoint good = replayed.ToCheckpoint()!;
        CheckpointState first = good.States[0];
        ProgressCheckpoint damaged = damage switch
        {
            "another version" => good with { Version = 2 },
            "another algorithm" => good with { Algorithm = "sm2-replay-0" },
            "another log state" => good with { Fingerprint = "64:abc" },
            "odd minutes" => good with { Minutes = good.Minutes[..^1] },
            "not hex" => good with { Minutes = new string('z', good.Minutes.Length) },
            // A gap of zero minutes: the same minute twice.
            "minutes not ascending" => good with { Minutes = good.Minutes + "00" },
            // A number whose last byte still says "more follows", and one longer than any gap can be.
            "truncated minute" => good with { Minutes = good.Minutes + "80" },
            "oversized minute" => good with { Minutes = good.Minutes + "FFFFFFFFFF7F" },
            "unknown grade" => good with { States = [first with { Grade = "great" }, .. good.States.Skip(1)] },
            "malformed question id" => good with { States = [first with { Question = "not-an-id" }, .. good.States.Skip(1)] },
            _ => good with { Answered = -1 },
        };

        Assert.Null(LocalProgress.FromCheckpoint(damaged, "63:abc"));
    }

    [Fact]
    public void Append_GivesWhatAReplayOfTheWholeLogGives()
    {
        List<AnswerDto> log = MixedLog(out QuestionDto[] questions);
        LocalProgress before = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("63:abc");
        DateTimeOffset later = Now.AddDays(1);
        List<AnswerDto> appended =
        [
            Answer(questions[0], "partial", later),
            Answer(questions[1], "knew", later.AddMinutes(1)),
            // Not an answer: it counts for nothing, here as in a replay.
            Answer(questions[2], "great", later.AddMinutes(2)),
            Answer(questions[0], "knew", later.AddDays(1)),
        ];

        bool applied = before.TryAppend(appended, Scheduler, "67:def", out LocalProgress after);

        Assert.True(applied);
        Assert.Equal("67:def", after.Fingerprint);
        AssertSameProgress(LocalProgress.Replay([.. log, .. appended], Scheduler, Now), after);
        // The progress it started from is not changed.
        Assert.Equal(before.TotalAnswered, LocalProgress.Replay(log, Scheduler, Now).TotalAnswered);
    }

    [Fact]
    public void Append_IsRefused_WhenAReplayWouldPutTheAnswerBeforeAnotherOfItsQuestion()
    {
        List<AnswerDto> log = MixedLog(out QuestionDto[] questions);
        LocalProgress before = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("63:abc");

        // Older than the question's last answer: the replay applies it first, so the states after it would differ.
        bool applied = before.TryAppend([Answer(questions[0], "didnt-know", Now.AddDays(-40))], Scheduler, "64:def", out LocalProgress after);

        Assert.False(applied);
        Assert.Same(before, after);
    }

    [Fact]
    public void Append_OfAnotherQuestionEarlierThanTheLastAnswerOfTheLog_GivesWhatAReplayGives()
    {
        QuestionDto old = OpenQ;
        QuestionDto fresh = SingleQ;
        List<AnswerDto> log = [Answer(old, "knew", Now.AddDays(-1))];
        LocalProgress before = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("1:abc");
        List<AnswerDto> appended = [Answer(fresh, "knew", Now.AddDays(-5))];

        // A question never answered has no earlier answer to disturb, and the streak minutes are kept in order by insertion.
        bool applied = before.TryAppend(appended, Scheduler, "2:def", out LocalProgress after);

        Assert.True(applied);
        AssertSameProgress(LocalProgress.Replay([.. log, .. appended], Scheduler, Now), after);
    }

    [Fact]
    public void Append_AfterAFutureDatedAnswer_StillAppliesAnswersOfOtherQuestions()
    {
        // A device clock that was ahead: one answer lies days in the future. Every later answer (real time) must not fall
        // back to a replay of the whole log, only an answer to the same question has to.
        QuestionDto ahead = OpenQ;
        QuestionDto other = SingleQ;
        List<AnswerDto> log = [Answer(ahead, "knew", Now.AddDays(3))];
        LocalProgress before = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("1:abc");
        List<AnswerDto> appended = [Answer(other, "knew", Now), Answer(other, "partial", Now.AddMinutes(5))];

        bool applied = before.TryAppend(appended, Scheduler, "3:def", out LocalProgress after);

        Assert.True(applied);
        AssertSameProgress(LocalProgress.Replay([.. log, .. appended], Scheduler, Now), after);
        Assert.False(before.TryAppend([Answer(ahead, "partial", Now)], Scheduler, "2:ghi", out _));
    }

    [Fact]
    public void Append_SkipsAnswersOutsideTheSanityWindow()
    {
        List<AnswerDto> log = MixedLog(out QuestionDto[] questions);
        LocalProgress before = LocalProgress.Replay(log, Scheduler, Now).WithFingerprint("63:abc");
        List<AnswerDto> appended =
        [
            Answer(questions[0], "knew", new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero)),
            Answer(questions[1], "knew", DateTimeOffset.MinValue),
            Answer(questions[2], "knew", Now.AddDays(1)),
        ];

        bool applied = before.TryAppend(appended, Scheduler, "66:def", out LocalProgress after);

        Assert.True(applied);
        Assert.Equal(before.TotalAnswered + 1, after.TotalAnswered);
        AssertSameProgress(LocalProgress.Replay([.. log, .. appended], Scheduler, Now), after);
    }

    [Fact]
    public async Task ALogWithAnExtremeTime_LoadsAndItsCheckpointIsUsedByTheNextLoad()
    {
        StaticSiteFixture site = new();
        StaticSiteFixture.Tab writer = site.NewTab();
        List<AnswerDto> log =
        [
            Answer(OpenQ, "knew", Now.AddDays(-2)),
            Answer(SingleQ, "knew", new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero)),
            Answer(MultiQ, "partial", Now.AddDays(-1)),
        ];
        Assert.True(await writer.Outbox.AddAllAsync(StorageKeys.AnonymousOutbox, log));

        StaticSiteFixture.Tab first = site.NewTab();
        LocalProgress replayed = await first.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        StaticSiteFixture.Tab second = site.NewTab();
        LocalProgress restored = await second.Progress.GetAsync(Ct);

        Assert.Equal(2, replayed.TotalAnswered);
        Assert.Equal(0, second.Scheduler.Applied);
        AssertSameProgress(replayed, restored);
        await first.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now));
        Assert.Equal(3, (await first.Progress.GetAsync(Ct)).TotalAnswered);
    }

    [Fact]
    public void AlgorithmName_PinsTheBehaviourOfTheSchedulerAndTheReplay()
    {
        // A checkpoint stores states computed by the scheduler. If the scheduler or the replay changes, stored checkpoints
        // are wrong, and only a new algorithm name makes the client ignore them: change ProgressCheckpoint.CurrentAlgorithm
        // together with this value.
        StringBuilder probe = new();
        QuestionDto question = OpenQ;
        string[] grades = ["knew", "knew", "partial", "didnt-know", "knew", "knew", "knew", "partial", "knew"];
        List<AnswerDto> log = [];
        for (int i = 0; i < grades.Length; i++)
        {
            log.Add(Answer(question, grades[i], Now.AddDays(i * 2).AddHours(i), "01JD" + i.ToString("D22", CultureInfo.InvariantCulture)));
            LocalProgress progress = LocalProgress.Replay(log, Scheduler, Now);
            ReviewState state = progress.States[new QuestionId(question.Id)];
            probe.Append(CultureInfo.InvariantCulture, $"{state.DueAt.UtcTicks}|{state.IntervalDays}|{state.Ease}|{state.Repetitions}|{state.Lapses}|{state.LastGrade}|{state.LastAnsweredAt.UtcTicks};");
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(probe.ToString())))[..16];

        Assert.Equal("sm2-replay-1", ProgressCheckpoint.CurrentAlgorithm);
        Assert.Equal("1d502033f137cd49", hash);
    }

    private static async Task WaitForCheckpointAsync(StaticSiteFixture site, string? differentFrom = null)
    {
        for (int i = 0; i < 200; i++)
        {
            string? raw = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint);
            if (raw is not null && raw != differentFrom)
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail("The checkpoint was not written.");
    }

    private static async Task<StaticSiteFixture> SiteWithLogAsync(int days = 20)
    {
        StaticSiteFixture site = new();
        StaticSiteFixture.Tab writer = site.NewTab();
        QuestionDto[] questions = [OpenQ, SingleQ, MultiQ];
        List<AnswerDto> log = [];
        for (int day = 0; day < days; day++)
        {
            for (int i = 0; i < questions.Length; i++)
            {
                log.Add(Answer(questions[i], (day + i) % 3 == 0 ? "knew" : "partial", Now.AddDays(-days + day).AddMinutes(i * 11)));
            }
        }

        Assert.True(await writer.Outbox.AddAllAsync(StorageKeys.AnonymousOutbox, log));
        return site;
    }

    [Fact]
    public async Task Load_ReplaysOnce_ThenEveryOtherPageLoadUsesTheCheckpointWithoutReadingTheLog()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab first = site.NewTab();
        LocalProgress replayed = await first.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        Assert.NotEqual(0, first.Scheduler.Applied);

        StaticSiteFixture.Tab second = site.NewTab();
        second.Hook.Reads.Clear();
        LocalProgress restored = await second.Progress.GetAsync(Ct);

        // No answer was applied and the log was never read: the progress came from the checkpoint alone.
        Assert.Equal(0, second.Scheduler.Applied);
        Assert.DoesNotContain(StorageKeys.AnonymousOutbox, second.Hook.Reads);
        AssertSameProgress(replayed, restored);
    }

    [Fact]
    public async Task Load_ReplaysAgainAndRewritesTheCheckpoint_WhenTheLogMovedOn()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab first = site.NewTab();
        await first.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        string stale = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint)!;

        // Another tab that never loaded its progress appends; nobody updates the checkpoint.
        StaticSiteFixture.Tab writer = site.NewTab();
        await writer.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now.AddDays(1)));
        StaticSiteFixture.Tab third = site.NewTab();
        LocalProgress progress = await third.Progress.GetAsync(Ct);

        Assert.NotEqual(0, third.Scheduler.Applied);
        IReadOnlyList<AnswerDto> whole = await site.NewTab().Outbox.ReadAsync(StorageKeys.AnonymousOutbox);
        AssertSameProgress(LocalProgress.Replay(whole, Scheduler, Now), progress);
        await WaitForCheckpointAsync(site, stale);
        StaticSiteFixture.Tab fourth = site.NewTab();
        await fourth.Progress.GetAsync(Ct);
        Assert.Equal(0, fourth.Scheduler.Applied);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("not json")]
    public async Task Load_ReplaysTheLog_WhenTheCheckpointIsMissingOrDamaged(string kind)
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab first = site.NewTab();
        LocalProgress replayed = await first.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        if (kind == "missing")
        {
            await site.Store.MutateAsync(StoreOp.Delete(StorageKeys.AnonymousProgressCheckpoint));
        }
        else
        {
            site.Store.SetRaw(StorageKeys.AnonymousProgressCheckpoint, kind == "corrupt" ? "{\"version\":1,\"algorithm\":\"sm2-replay-1\",\"states\":7}" : "{{{");
        }

        string? damaged = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint);
        StaticSiteFixture.Tab second = site.NewTab();
        LocalProgress progress = await second.Progress.GetAsync(Ct);

        Assert.NotEqual(0, second.Scheduler.Applied);
        AssertSameProgress(replayed, progress);
        // The next load is fast again: the damaged copy was replaced.
        await WaitForCheckpointAsync(site, damaged);
        StaticSiteFixture.Tab third = site.NewTab();
        await third.Progress.GetAsync(Ct);
        Assert.Equal(0, third.Scheduler.Applied);
    }

    [Fact]
    public async Task Load_NeverTrustsACheckpointOfAnotherLog()
    {
        StaticSiteFixture site = await SiteWithLogAsync(days: 20);
        StaticSiteFixture.Tab first = site.NewTab();
        await first.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        string checkpoint = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint)!;

        // The log is replaced by a shorter one (storage cleared and another set of answers written), the checkpoint stays.
        StaticSiteFixture other = await SiteWithLogAsync(days: 5);
        other.Store.SetRaw(StorageKeys.AnonymousProgressCheckpoint, checkpoint);
        StaticSiteFixture.Tab tab = other.NewTab();
        LocalProgress progress = await tab.Progress.GetAsync(Ct);

        Assert.NotEqual(0, tab.Scheduler.Applied);
        Assert.Equal(15, progress.TotalAnswered);
    }

    [Fact]
    public async Task OwnAnswer_IsAppliedToTheMemoWithoutReplayingTheLog_AndTheCheckpointFollows()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        LocalProgress before = await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        string stale = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint)!;
        int applied = tab.Scheduler.Applied;
        tab.Hook.Reads.Clear();
        QuestionDto question = OpenQ;

        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(question, "didnt-know", Now.AddDays(2)));
        LocalProgress after = await tab.Progress.GetAsync(Ct);

        Assert.Equal(applied + 1, tab.Scheduler.Applied);
        Assert.DoesNotContain(StorageKeys.AnonymousOutbox, tab.Hook.Reads);
        Assert.Equal(before.TotalAnswered + 1, after.TotalAnswered);
        Assert.Equal(Grade.DidntKnow, after.States[new QuestionId(question.Id)].LastGrade);
        await WaitForCheckpointAsync(site, stale);
        StaticSiteFixture.Tab next = site.NewTab();
        LocalProgress restored = await next.Progress.GetAsync(Ct);
        Assert.Equal(0, next.Scheduler.Applied);
        AssertSameProgress(after, restored);
        AssertSameProgress(LocalProgress.Replay(await next.Outbox.ReadAsync(StorageKeys.AnonymousOutbox), Scheduler, Now), restored);
    }

    [Fact]
    public async Task OwnAnswer_IsReplayedFromTheLog_WhenAnotherTabWroteFirst()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab mine = site.NewTab();
        StaticSiteFixture.Tab other = site.NewTab();
        await mine.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        QuestionDto open = OpenQ;
        QuestionDto single = SingleQ;

        // The other tab's answer is not known to this tab's memo, so this tab's own answer cannot be applied on top of it.
        await other.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(single, "knew", Now.AddDays(1)));
        await mine.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(open, "knew", Now.AddDays(2)));
        LocalProgress progress = await mine.Progress.GetAsync(Ct);

        // Both answers are in: the memo was dropped and the progress comes from the log as it is.
        Assert.Equal(20 * 3 + 2, progress.TotalAnswered);
        AssertSameProgress(LocalProgress.Replay(await mine.Outbox.ReadAsync(StorageKeys.AnonymousOutbox), Scheduler, Now), progress);
    }

    [Fact]
    public async Task OwnAnswer_ThatIsOlderThanTheLastOfItsQuestion_IsReplayedInItsPlace()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);

        // A backdated answer takes its chronological place (spec 5.6): applying it on top would give other states.
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "didnt-know", Now.AddDays(-25).AddMinutes(1)));
        LocalProgress progress = await tab.Progress.GetAsync(Ct);

        AssertSameProgress(LocalProgress.Replay(await tab.Outbox.ReadAsync(StorageKeys.AnonymousOutbox), Scheduler, Now), progress);
    }

    [Fact]
    public async Task UnsavedAnswers_AreCountedAndNeverStoredInTheCheckpoint()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        Assert.NotNull(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));

        // The write fails (quota), so the answer lives only in this tab; the progress must still show it.
        site.Store.QuotaFull = true;
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "didnt-know", Now.AddDays(2)));
        LocalProgress progress = await tab.Progress.GetAsync(Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(20 * 3 + 1, progress.TotalAnswered);
        Assert.Equal(1, tab.Outbox.UnsavedCount);
        // The refused append evicted the checkpoint to make room, and nothing wrote one that includes the unsaved answer.
        Assert.Null(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));
        // Another tab does not see it, and rightly so: it is not in the log.
        Assert.Equal(20 * 3, (await site.NewTab().Progress.GetAsync(Ct)).TotalAnswered);
    }

    [Fact]
    public async Task WithoutAFingerprint_TheLogIsReplayedAndNoCheckpointIsWritten()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab writer = site.NewTab();
        HookStore blind = new(site.Store);
        OutboxStore outbox = new(new NoFingerprintStore(blind));
        // A zero delay writes at once, so the check below can fail: with the default wait it would pass whatever the cache did.
        using LocalProgressCache cache = new(outbox, new NoFingerprintStore(blind), Scheduler, site.Time, TimeSpan.Zero);

        LocalProgress progress = await cache.GetAsync(Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(20 * 3, progress.TotalAnswered);
        Assert.Null(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));
        Assert.NotNull(writer);
    }

    [Fact]
    public async Task ChangedElsewhere_ReloadsFromTheOtherTabsCheckpoint()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab mine = site.NewTab();
        StaticSiteFixture.Tab other = site.NewTab();
        await mine.Progress.GetAsync(Ct);
        await other.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);
        string stale = site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint)!;

        await other.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now.AddDays(1)));
        await WaitForCheckpointAsync(site, stale);
        int applied = mine.Scheduler.Applied;
        mine.Hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox);
        LocalProgress progress = await mine.Progress.GetAsync(Ct);

        // The other tab already stored the new state; this tab took it instead of replaying twenty days of answers.
        Assert.Equal(applied, mine.Scheduler.Applied);
        Assert.Equal(20 * 3 + 1, progress.TotalAnswered);
    }

    [Fact]
    public async Task ADroppedMemo_CancelsTheQueuedCheckpoint_SoAStaleOneNeverReplacesAFresherOne()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab other = site.NewTab();
        HookStore hook = new(site.Store);
        OutboxStore outbox = new(hook);
        using LocalProgressCache cache = new(outbox, hook, Scheduler, site.Time, TimeSpan.FromMilliseconds(300));

        // The load queues a checkpoint for the log as it is now, to be written 300 ms later.
        await cache.GetAsync(Ct);
        await other.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now.AddDays(1)));
        hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox);
        await Task.Delay(700, Ct);

        // The other tab's write made that progress stale; writing it would put a checkpoint nobody can use over a fresher one.
        Assert.Null(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));
    }

    [Fact]
    public async Task ALoadWhoseLogIsChangedByAnotherTabAfterItsLastFingerprint_QueuesNoCheckpoint()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        HookStore hook = new(site.Store);
        // The storage event for another tab's write arrives right after the load took its closing fingerprint.
        EventAfterFingerprintStore store = new(hook, 2, () => hook.RaiseChangedElsewhere(StorageKeys.AnonymousOutbox));
        OutboxStore outbox = new(store);
        using LocalProgressCache cache = new(outbox, store, Scheduler, site.Time, TimeSpan.Zero);

        await cache.GetAsync(Ct);
        await Task.Delay(100, Ct);

        // The load's progress matches the fingerprints it took, but the event says they are already out of date.
        Assert.Null(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));
    }

    [Fact]
    public async Task AnAnswerThatDoesNotFitBesideTheCheckpoint_EvictsItAndIsSavedToTheLog()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);

        // One shared quota: the append is refused for as long as the checkpoint occupies its share.
        tab.Hook.Intercept = op => ValueTask.FromResult(
            op.Op == "append" && site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint) is not null ? StoreResult.Failed(StoreErrors.Quota) : null);
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now.AddDays(1)));

        Assert.Equal(0, tab.Outbox.UnsavedCount);
        Assert.Equal(StorageStatus.Persistent, tab.Outbox.Status);
        Assert.Equal(20 * 3 + 1, (await site.NewTab().Outbox.ReadAsync(StorageKeys.AnonymousOutbox)).Count);
    }

    [Fact]
    public async Task WhenEvictingTheCheckpointDoesNotHelp_TheAnswerStaysUnsavedAndTheAppendIsTriedOnlyTwice()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);

        int appends = 0;
        tab.Hook.Intercept = op =>
        {
            if (op.Op == "append")
            {
                appends++;
                return ValueTask.FromResult<StoreResult?>(StoreResult.Failed(StoreErrors.Quota));
            }

            return ValueTask.FromResult<StoreResult?>(null);
        };
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(OpenQ, "knew", Now.AddDays(1)));

        Assert.Equal(2, appends);
        Assert.Equal(1, tab.Outbox.UnsavedCount);
        Assert.Equal(StorageStatus.Full, tab.Outbox.Status);
    }

    [Fact]
    public async Task AMockThatDoesNotFitBesideTheCheckpoint_EvictsItAndIsSavedToTheHistory()
    {
        StaticSiteFixture site = await SiteWithLogAsync();
        StaticSiteFixture.Tab tab = site.NewTab();
        await tab.Progress.GetAsync(Ct);
        await WaitForCheckpointAsync(site);

        tab.Hook.Intercept = op => ValueTask.FromResult(
            op.Op == "historyAdd" && site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint) is not null ? StoreResult.Failed(StoreErrors.Quota) : null);
        StoreResult result = await tab.History.AddAsync("dotnet", new MockHistoryDto("01KD0000000000000000000001", "middle", 9, Now, 5, 2, 1, 1));

        Assert.True(result.Persisted);
        Assert.Equal(StorageStatus.Persistent, tab.History.Status);
        Assert.Null(site.Store.Raw(StorageKeys.AnonymousProgressCheckpoint));
        Assert.Single(await site.NewTab().History.ReadAsync("dotnet"));
    }

    private sealed class NoFingerprintStore(IBrowserStore inner) : IBrowserStore
    {
        public ValueTask<StoreResult> MutateAsync(StoreOp op) => inner.MutateAsync(op);

        public ValueTask<string?> ReadAsync(string key) => inner.ReadAsync(key);

        public ValueTask<IAsyncDisposable> WatchKeyAsync(string key, Action onChanged) => inner.WatchKeyAsync(key, onChanged);

        public ValueTask<string?> FingerprintAsync(string key) => ValueTask.FromResult<string?>(null);
    }

    private sealed class EventAfterFingerprintStore(IBrowserStore inner, int call, Action raise) : IBrowserStore
    {
        private int _fingerprints;

        public ValueTask<StoreResult> MutateAsync(StoreOp op) => inner.MutateAsync(op);

        public ValueTask<string?> ReadAsync(string key) => inner.ReadAsync(key);

        public ValueTask<IAsyncDisposable> WatchKeyAsync(string key, Action onChanged) => inner.WatchKeyAsync(key, onChanged);

        public async ValueTask<string?> FingerprintAsync(string key)
        {
            string? fingerprint = await inner.FingerprintAsync(key);
            if (++_fingerprints == call)
            {
                raise();
            }

            return fingerprint;
        }
    }
}
