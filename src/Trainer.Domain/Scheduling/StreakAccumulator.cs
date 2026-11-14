namespace Trainer.Domain.Scheduling;

/// <summary>
/// Counts consecutive answer days. Feed distinct or repeated days newest first; <see cref="Add"/> returns false once the
/// streak can no longer grow, so the caller stops reading. A streak is alive only if its newest day is today or yesterday.
/// </summary>
public sealed class StreakAccumulator(DateOnly today)
{
    private DateOnly? _last;
    private bool _stopped;

    public int Days { get; private set; }

    /// <summary>Adds the next day. Days after today and repeats of the previous day are ignored (returns true).</summary>
    public bool Add(DateOnly day)
    {
        if (_stopped)
        {
            return false;
        }

        if (day > today)
        {
            return true;
        }

        if (_last is null)
        {
            if (day.DayNumber < today.DayNumber - 1)
            {
                _stopped = true;
                return false;
            }
        }
        else if (day == _last.Value)
        {
            return true;
        }
        else if (day.DayNumber != _last.Value.DayNumber - 1)
        {
            _stopped = true;
            return false;
        }

        _last = day;
        Days++;
        return true;
    }
}
