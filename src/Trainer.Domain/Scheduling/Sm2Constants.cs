namespace Trainer.Domain.Scheduling;

/// <summary>Tuning of the SM-2 variant. With these values "knew" answers give intervals of 1, 3, 7, 16, 37, 85, 196, 365 days.</summary>
public static class Sm2Constants
{
    public const decimal InitialEase = 2.3m;
    public const decimal MinEase = 1.3m;
    public const decimal PartialEasePenalty = 0.15m;
    public const decimal LapseEasePenalty = 0.20m;
    public const int MaxIntervalDays = 365;
}
