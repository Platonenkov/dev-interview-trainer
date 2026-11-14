using Trainer.Domain.Grading;
using Trainer.Domain.Scheduling;

namespace Trainer.Domain.Tests;

public sealed class Sm2ReviewSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = T0.AddDays(400);
    private static readonly Sm2ReviewScheduler S = new();

    private static ReviewState Mature(int interval = 7, decimal ease = 2.3m, int reps = 3, int lapses = 0, Grade lastGrade = Grade.Knew) =>
        new(T0.AddDays(interval), interval, ease, reps, lapses, lastGrade, T0);

    private static int[] Intervals(params Grade[] grades)
    {
        List<int> result = [];
        ReviewState? state = null;
        DateTimeOffset at = T0;
        foreach (Grade grade in grades)
        {
            state = S.Apply(state, grade, at);
            result.Add(state.IntervalDays);
            at = state.DueAt;
        }

        return [.. result];
    }

    [Fact]
    public void FirstAnswer_CreatesState()
    {
        ReviewState s = S.Apply(null, Grade.Knew, T0);
        Assert.Equal((1, 1, T0.AddDays(1)), (s.IntervalDays, s.Repetitions, s.DueAt));
        Assert.Equal((Sm2Constants.InitialEase, Grade.Knew, T0), (s.Ease, s.LastGrade, s.LastAnsweredAt));
    }

    [Fact]
    public void KnewSequence_Is_1_3_7_16_37() =>
        Assert.Equal([1, 3, 7, 16, 37], Intervals(Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew));

    [Fact]
    public void KnewSequence_ReachesCapAt365() =>
        Assert.Equal(
            [1, 3, 7, 16, 37, 85, 196, 365, 365],
            Intervals(Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew, Grade.Knew));

    [Fact]
    public void DidntKnow_ResetsToOneDay_AndCountsLapse()
    {
        ReviewState s = S.Apply(Mature(interval: 16), Grade.DidntKnow, Due);
        Assert.Equal((1, 0, 1), (s.IntervalDays, s.Repetitions, s.Lapses));
    }

    [Fact]
    public void FirstAnswer_DidntKnow_CountsLapse()
    {
        ReviewState s = S.Apply(null, Grade.DidntKnow, T0);
        Assert.Equal((1, 0, 1, 2.1m), (s.IntervalDays, s.Repetitions, s.Lapses, s.Ease));
    }

    [Fact]
    public void Partial_HalvesInterval_AndLowersEase()
    {
        ReviewState s = S.Apply(Mature(interval: 16, ease: 2.3m), Grade.Partial, Due);
        Assert.Equal((8, 2.15m), (s.IntervalDays, s.Ease));
        Assert.Equal(3, s.Repetitions);
    }

    [Fact]
    public void Ease_NeverBelowMinimum() => Assert.Equal(1.3m, S.Apply(Mature(ease: 1.35m), Grade.DidntKnow, Due).Ease);

    [Fact]
    public void Interval_CappedAt365() => Assert.Equal(365, S.Apply(Mature(interval: 300, reps: 6), Grade.Knew, Due).IntervalDays);

    [Fact]
    public void Knew_GrowsAtLeastOneDay_WhenEaseIsLow() =>
        Assert.Equal(4, S.Apply(Mature(interval: 3, ease: 1.3m, reps: 2), Grade.Knew, Due).IntervalDays);

    [Fact]
    public void Apply_KnewBeforeDue_KeepsScheduleAdvancesMetadata()
    {
        ReviewState c = Mature(interval: 7, lastGrade: Grade.DidntKnow);
        DateTimeOffset at = c.DueAt.AddDays(-3);
        ReviewState s = S.Apply(c, Grade.Knew, at);
        Assert.Equal((c.DueAt, c.IntervalDays, c.Ease, c.Repetitions, c.Lapses), (s.DueAt, s.IntervalDays, s.Ease, s.Repetitions, s.Lapses));
        Assert.Equal((Grade.Knew, at), (s.LastGrade, s.LastAnsweredAt));
    }

    [Fact]
    public void Apply_EarlyKnewThenDelayedFailure_FailureIsStale()
    {
        ReviewState c = Mature(interval: 7);
        DateTimeOffset t2 = c.DueAt.AddDays(-2);
        DateTimeOffset t1 = t2.AddHours(-1);
        ReviewState k = S.Apply(c, Grade.Knew, t2);
        Assert.Same(k, S.Apply(k, Grade.DidntKnow, t1));
    }

    [Fact]
    public void Apply_EqualTimestamp_IsStale()
    {
        ReviewState c = Mature();
        Assert.Same(c, S.Apply(c, Grade.DidntKnow, c.LastAnsweredAt));
    }

    [Fact]
    public void Apply_DidntKnowJustBeforeDue_ResetsIntervalAndCountsLapse()
    {
        ReviewState c = Mature(interval: 16, reps: 4, lapses: 0, ease: 2.3m);
        ReviewState s = S.Apply(c, Grade.DidntKnow, c.DueAt.AddHours(-12));
        Assert.Equal((1, 0, 1, 2.1m), (s.IntervalDays, s.Repetitions, s.Lapses, s.Ease));
        Assert.Equal(c.DueAt, s.DueAt);
        Assert.Equal(1, S.Apply(s, Grade.Knew, s.DueAt).IntervalDays);
    }

    [Fact]
    public void Apply_PartialBeforeDue_WhoseResultIsLater_KeepsDueButLowersEase()
    {
        ReviewState c = S.Apply(null, Grade.Knew, T0);
        ReviewState s = S.Apply(c, Grade.Partial, T0.AddHours(12));
        Assert.Equal((T0.AddDays(1), 1, 2.15m, 1, 0), (s.DueAt, s.IntervalDays, s.Ease, s.Repetitions, s.Lapses));
    }

    [Fact]
    public void Apply_DidntKnowLongBeforeDue_MovesDueEarlier()
    {
        ReviewState c = Mature(interval: 16);
        ReviewState s = S.Apply(c, Grade.DidntKnow, c.DueAt.AddDays(-10));
        Assert.Equal(c.DueAt.AddDays(-9), s.DueAt);
    }

    [Fact]
    public void Apply_AnswerExactlyAtDue_IsDueAndTakesTheNormalStep()
    {
        ReviewState c = Mature(interval: 7);
        ReviewState s = S.Apply(c, Grade.Knew, c.DueAt);
        Assert.Equal((16, 4, c.DueAt.AddDays(16)), (s.IntervalDays, s.Repetitions, s.DueAt));
    }

    [Fact]
    public void Apply_PartialLongBeforeDue_MovesDueEarlier()
    {
        ReviewState c = Mature(interval: 16);
        ReviewState s = S.Apply(c, Grade.Partial, c.DueAt.AddDays(-10));
        Assert.Equal((8, c.DueAt.AddDays(-2)), (s.IntervalDays, s.DueAt));
    }

    [Fact]
    public void Apply_StaleAnswer_ReturnsCurrentUnchanged()
    {
        ReviewState c = Mature();
        Assert.Same(c, S.Apply(c, Grade.DidntKnow, c.LastAnsweredAt.AddMinutes(-1)));
    }

    [Fact]
    public void Apply_UndefinedGrade_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => S.Apply(null, (Grade)99, T0));
}
