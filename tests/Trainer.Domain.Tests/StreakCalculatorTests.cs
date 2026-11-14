using System.Collections;
using Trainer.Domain.Scheduling;

namespace Trainer.Domain.Tests;

public sealed class StreakCalculatorTests
{
    private static DateOnly D(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd");

    private static IEnumerable<DateOnly> Days(DateOnly newest, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return newest.AddDays(-i);
        }
    }

    [Theory]
    [InlineData("2026-11-12", 3)]
    [InlineData("2026-11-13", 3)]
    [InlineData("2026-11-14", 0)]
    public void Streak_EndsTodayOrYesterday(string today, int expected) =>
        Assert.Equal(expected, StreakCalculator.Compute([D("2026-11-12"), D("2026-11-11"), D("2026-11-10")], D(today)));

    [Fact]
    public void Streak_AtTheEdgesOfTheDateRange_DoesNotThrow()
    {
        Assert.Equal(1, StreakCalculator.Compute([DateOnly.MinValue], DateOnly.MinValue));
        Assert.Equal(0, StreakCalculator.Compute([DateOnly.MinValue], DateOnly.MaxValue));
        Assert.Equal(2, StreakCalculator.Compute([DateOnly.MaxValue, DateOnly.MaxValue.AddDays(-1)], DateOnly.MaxValue));
        Assert.Equal(1, StreakCalculator.Compute([DateOnly.MinValue, DateOnly.MinValue], DateOnly.MinValue));
    }

    [Fact]
    public void Streak_GapBreaksIt() => Assert.Equal(1, StreakCalculator.Compute([D("2026-11-12"), D("2026-11-09")], D("2026-11-12")));

    [Fact]
    public void Streak_Empty_IsZero() => Assert.Equal(0, StreakCalculator.Compute([], D("2026-11-12")));

    [Fact]
    public void Streak_LongerThan400Days_IsCounted() =>
        Assert.Equal(500, StreakCalculator.Compute(Days(D("2026-11-12"), 500), D("2026-11-12")));

    [Fact]
    public void Streak_StopsReadingAtFirstGap()
    {
        CountingEnumerable<DateOnly> days = new([D("2026-11-12"), D("2026-11-10"), D("2026-11-09")]);
        StreakCalculator.Compute(days, D("2026-11-12"));
        Assert.Equal(2, days.Read);
    }

    [Fact]
    public void Accumulator_ReturnsFalseAtGap_AndIgnoresDuplicates()
    {
        StreakAccumulator accumulator = new(D("2026-11-12"));
        Assert.True(accumulator.Add(D("2026-11-12")));
        Assert.True(accumulator.Add(D("2026-11-12")));
        Assert.True(accumulator.Add(D("2026-11-11")));
        Assert.False(accumulator.Add(D("2026-11-09")));
        Assert.Equal(2, accumulator.Days);
    }

    [Fact]
    public void Accumulator_StaysStoppedAfterFalse()
    {
        StreakAccumulator accumulator = new(D("2026-11-12"));
        Assert.True(accumulator.Add(D("2026-11-12")));
        Assert.False(accumulator.Add(D("2026-11-09")));
        Assert.False(accumulator.Add(D("2026-11-11")));
        Assert.Equal(1, accumulator.Days);
    }

    [Fact]
    public void Accumulator_IgnoresDaysAfterToday()
    {
        StreakAccumulator accumulator = new(D("2026-11-12"));
        Assert.True(accumulator.Add(D("2026-11-13")));
        Assert.True(accumulator.Add(D("2026-11-12")));
        Assert.Equal(1, accumulator.Days);
    }

    private sealed class CountingEnumerable<T>(IReadOnlyList<T> items) : IEnumerable<T>
    {
        public int Read { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (T item in items)
            {
                Read++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
