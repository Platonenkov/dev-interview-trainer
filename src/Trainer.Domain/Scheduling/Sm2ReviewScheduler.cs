using Trainer.Domain.Grading;

namespace Trainer.Domain.Scheduling;

/// <summary>
/// SM-2 variant described in docs/architecture.md (Review scheduling). <c>answeredAt.AddDays(interval)</c> throws near
/// <see cref="DateTimeOffset.MaxValue"/>; callers must pass a time the answer time policy accepted (ADR 0005 rejects
/// anything more than 5 minutes ahead of the server clock), which keeps it far from the limit.
/// </summary>
public sealed class Sm2ReviewScheduler : IReviewScheduler
{
    public ReviewState Apply(ReviewState? current, Grade grade, DateTimeOffset answeredAt)
    {
        if (current is not null && answeredAt <= current.LastAnsweredAt)
        {
            return current;
        }

        if (current is not null && answeredAt < current.DueAt && grade == Grade.Knew)
        {
            // A correct answer to a question that is not due yet is no evidence for a longer interval, but it must
            // still become the latest answer so an older delayed failure is recognised as stale.
            return current with { LastGrade = grade, LastAnsweredAt = answeredAt };
        }

        ReviewState next = Step(current, grade, answeredAt);
        if (current is not null && answeredAt < current.DueAt)
        {
            // Only the due date is clamped: interval, ease, repetitions and lapses keep the reset, otherwise a failure
            // shortly before the due date would be lost.
            return next with { DueAt = current.DueAt < next.DueAt ? current.DueAt : next.DueAt };
        }

        return next;
    }

    private static ReviewState Step(ReviewState? current, Grade grade, DateTimeOffset answeredAt)
    {
        int interval = current?.IntervalDays ?? 0;
        decimal ease = current?.Ease ?? Sm2Constants.InitialEase;
        int repetitions = current?.Repetitions ?? 0;
        int lapses = current?.Lapses ?? 0;

        switch (grade)
        {
            case Grade.Knew:
                interval = repetitions switch
                {
                    0 => 1,
                    1 => 3,
                    _ => Math.Max(interval + 1, RoundToDays(interval * ease)),
                };
                repetitions++;
                break;
            case Grade.Partial:
                interval = Math.Max(1, RoundToDays(interval * 0.5m));
                ease = Math.Max(Sm2Constants.MinEase, ease - Sm2Constants.PartialEasePenalty);
                break;
            case Grade.DidntKnow:
                interval = 1;
                repetitions = 0;
                lapses++;
                ease = Math.Max(Sm2Constants.MinEase, ease - Sm2Constants.LapseEasePenalty);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(grade), grade, null);
        }

        interval = Math.Min(interval, Sm2Constants.MaxIntervalDays);
        return new ReviewState(answeredAt.AddDays(interval), interval, ease, repetitions, lapses, grade, answeredAt);
    }

    private static int RoundToDays(decimal days) => (int)Math.Round(days, MidpointRounding.AwayFromZero);
}
