namespace Trainer.Application.Answers;

/// <summary>
/// Judges client-reported times against the server clock (ADR 0005). Only the server's own time is shifted and the
/// client value is merely compared, so <see cref="DateTimeOffset.MinValue"/> and <see cref="DateTimeOffset.MaxValue"/>
/// cannot overflow.
/// </summary>
public static class AnswerTimePolicy
{
    /// <summary>Older reported times are rejected (spec 8.8: "not older than 1 year").</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// How far ahead of the server a reported time may be and still be accepted (clamped). The client calibrates its
    /// clock from the 1-second <c>Date</c> header, so a correct answer can look slightly ahead.
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static TimeVerdict Judge(DateTimeOffset answeredAt, DateTimeOffset serverNow)
    {
        if (answeredAt < serverNow - MaxAge)
        {
            return TimeVerdict.TooOld;
        }

        if (answeredAt > serverNow + FutureTolerance)
        {
            return TimeVerdict.InFuture;
        }

        return answeredAt > serverNow ? TimeVerdict.Clamp : TimeVerdict.Accept;
    }

    /// <summary>The time to store: <paramref name="answeredAt"/> when accepted, <paramref name="serverNow"/> when clamped.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The time is rejected by <see cref="Judge"/>; judge before storing.</exception>
    public static DateTimeOffset Effective(DateTimeOffset answeredAt, DateTimeOffset serverNow) =>
        Judge(answeredAt, serverNow) switch
        {
            TimeVerdict.Accept => answeredAt,
            TimeVerdict.Clamp => serverNow,
            TimeVerdict verdict => throw new ArgumentOutOfRangeException(
                nameof(answeredAt), verdict, "The reported time is rejected by the answer time policy."),
        };
}
