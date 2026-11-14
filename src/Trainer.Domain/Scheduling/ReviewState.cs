using Trainer.Domain.Grading;

namespace Trainer.Domain.Scheduling;

/// <summary>
/// Per-user, per-question review state. <paramref name="DueAt"/>, <paramref name="IntervalDays"/>,
/// <paramref name="Ease"/>, <paramref name="Repetitions"/> and <paramref name="Lapses"/> are the schedule;
/// <paramref name="LastGrade"/> and <paramref name="LastAnsweredAt"/> describe the latest applied answer.
/// </summary>
public sealed record ReviewState(
    DateTimeOffset DueAt,
    int IntervalDays,
    decimal Ease,
    int Repetitions,
    int Lapses,
    Grade LastGrade,
    DateTimeOffset LastAnsweredAt);
