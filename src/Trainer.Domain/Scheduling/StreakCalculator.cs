namespace Trainer.Domain.Scheduling;

/// <summary>Streak over an in-memory sequence; infrastructure streams days into <see cref="StreakAccumulator"/> directly.</summary>
public static class StreakCalculator
{
    /// <param name="answerDaysDescending">Answer days, newest first. Enumeration stops at the first gap.</param>
    /// <param name="today">The user's local date.</param>
    public static int Compute(IEnumerable<DateOnly> answerDaysDescending, DateOnly today)
    {
        StreakAccumulator accumulator = new(today);
        foreach (DateOnly day in answerDaysDescending)
        {
            if (!accumulator.Add(day))
            {
                break;
            }
        }

        return accumulator.Days;
    }
}
