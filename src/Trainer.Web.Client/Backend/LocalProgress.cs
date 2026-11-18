using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// The static edition's progress: the review state of every question, recomputed from the whole answer log (spec 5.6).
/// The log is the only source of truth, so the same log gives the same states on every reload and a late or backdated
/// answer simply takes its chronological place. States are kept for every question id in the log, whatever its stack or
/// language; the index only filters them for selection and display. A <see cref="ProgressCheckpoint"/> is a stored copy of
/// the result for one log state, which a page load can use instead of replaying a long log.
/// </summary>
public sealed class LocalProgress
{
    private readonly Dictionary<QuestionId, ReviewState> _states;

    // The distinct minutes (Unix time / 60) that carry an answer, ascending; the streak reads the days from the end. A minute
    // decides the local day exactly for every UTC offset (an offset is whole minutes), and repeats of a day change nothing.
    private readonly int[] _answerMinutes;

    private readonly int _totalAnswered;

    private LocalProgress(Dictionary<QuestionId, ReviewState> states, int[] answerMinutes, int totalAnswered, string? fingerprint)
    {
        _states = states;
        _answerMinutes = answerMinutes;
        _totalAnswered = totalAnswered;
        Fingerprint = fingerprint;
    }

    /// <summary>
    /// The earliest <c>answeredAt</c> that counts as an answer. ADR 0005 judges a reported time against the clock when the
    /// answer is recorded (not older than a year, at most five minutes ahead); a replay must instead be a pure function of
    /// the log, because a checkpoint is keyed by the log alone, and an answer must not age out of the progress, so the replay
    /// only applies an absolute sanity window around it.
    /// </summary>
    public static readonly DateTimeOffset EarliestAnswer = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The latest <c>answeredAt</c> that counts as an answer, far enough ahead for a clock that was wrong for a while and
    /// close enough that the scheduler's <c>AddDays</c> and the minute arithmetic cannot overflow (<see cref="EarliestAnswer"/>).
    /// </summary>
    public static readonly DateTimeOffset LatestAnswer = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static LocalProgress Empty { get; } = new([], [], 0, null);

    /// <summary>
    /// The state of the answer log these states were computed from, as <c>outbox.js</c> fingerprints it (entry count and the
    /// tail of the log), or null when it is not known. A checkpoint is valid for exactly this value.
    /// </summary>
    public string? Fingerprint { get; }

    /// <summary>One review state per answered question id, in no particular order.</summary>
    public IReadOnlyDictionary<QuestionId, ReviewState> States => _states;

    /// <summary>How many valid answers were replayed.</summary>
    public int TotalAnswered => _totalAnswered;

    /// <summary>
    /// Applies every valid answer of <paramref name="log"/> through <paramref name="scheduler"/>, ordered by
    /// <see cref="AnswerDto.AnsweredAt"/> and then by <see cref="AnswerDto.ClientAnswerId"/> (ordinal). The time is the one
    /// fixed when the answer entered the log: it is never clamped or moved here, so a device clock corrected later cannot
    /// shift an anchor. An entry without a time, with a time outside <see cref="EarliestAnswer"/> to <see cref="LatestAnswer"/>,
    /// with an unknown grade or with a malformed question id is not an answer.
    /// </summary>
    /// <param name="now">Not used by the replay itself; due checks happen when the states are read.</param>
    public static LocalProgress Replay(IReadOnlyList<AnswerDto> log, IReviewScheduler scheduler, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(scheduler);

        List<(DateTimeOffset At, string Id, QuestionId Question, Grade Grade)> answers = ValidAnswers(log);
        Dictionary<QuestionId, ReviewState> states = [];
        foreach ((DateTimeOffset at, _, QuestionId question, Grade grade) in answers)
        {
            states[question] = scheduler.Apply(states.GetValueOrDefault(question), grade, at);
        }

        return new LocalProgress(states, MinutesOf(answers.Select(a => a.At)), answers.Count, null);
    }

    /// <summary>The same progress, known to belong to the log state <paramref name="fingerprint"/>.</summary>
    public LocalProgress WithFingerprint(string? fingerprint) => new(_states, _answerMinutes, _totalAnswered, fingerprint);

    /// <summary>
    /// Applies answers appended after everything this progress has seen, without replaying the log. Returns false, leaving
    /// the caller to replay, when an answer is not strictly later than the last answer of its question (the replay would have
    /// put it before that one): only then is applying it on top the same as replaying. An answer in an earlier minute than
    /// the last of the log (a device clock that was ahead) only adds its minute in place; other questions are unaffected.
    /// Entries that are not answers count for nothing, as in <see cref="Replay"/>.
    /// </summary>
    public bool TryAppend(IReadOnlyList<AnswerDto> appended, IReviewScheduler scheduler, string? fingerprint, out LocalProgress next)
    {
        ArgumentNullException.ThrowIfNull(appended);
        ArgumentNullException.ThrowIfNull(scheduler);
        next = this;

        List<(DateTimeOffset At, string Id, QuestionId Question, Grade Grade)> answers = ValidAnswers(appended);
        Dictionary<QuestionId, ReviewState> states = new(_states);
        // AddRange copies the array in bulk: a collection expression would enumerate every element, which costs real time in
        // the interpreter for a log of ten thousand answers, on every answer.
        List<int> minutes = new(_answerMinutes.Length + answers.Count);
        minutes.AddRange(_answerMinutes);
        foreach ((DateTimeOffset at, _, QuestionId question, Grade grade) in answers)
        {
            if (states.TryGetValue(question, out ReviewState? last) && at <= last.LastAnsweredAt)
            {
                return false;
            }

            states[question] = scheduler.Apply(states.GetValueOrDefault(question), grade, at);
            int index = minutes.BinarySearch(MinuteOf(at));
            if (index < 0)
            {
                minutes.Insert(~index, MinuteOf(at));
            }
        }

        int[] merged = minutes.ToArray();
        next = new LocalProgress(states, merged, _totalAnswered + answers.Count, fingerprint);
        return true;
    }

    /// <summary>The progress written as a checkpoint for the log state <see cref="Fingerprint"/>; null when that is not known.</summary>
    public ProgressCheckpoint? ToCheckpoint()
    {
        if (Fingerprint is null)
        {
            return null;
        }

        CheckpointState[] states =
        [
            .. _states.Select(entry => new CheckpointState(
                entry.Key.Value,
                entry.Value.DueAt.UtcTicks,
                entry.Value.IntervalDays,
                entry.Value.Ease,
                entry.Value.Repetitions,
                entry.Value.Lapses,
                GradeKeys.ToKey(entry.Value.LastGrade),
                entry.Value.LastAnsweredAt.UtcTicks)),
        ];
        return new ProgressCheckpoint(ProgressCheckpoint.CurrentVersion, ProgressCheckpoint.CurrentAlgorithm, Fingerprint, _totalAnswered, EncodeMinutes(_answerMinutes), states);
    }

    /// <summary>
    /// Restores a checkpoint, or null when it is not one this build wrote for <paramref name="fingerprint"/> (another
    /// version or algorithm, another log state, or content that does not parse), so the caller replays the log instead.
    /// </summary>
    public static LocalProgress? FromCheckpoint(ProgressCheckpoint? checkpoint, string fingerprint)
    {
        if (checkpoint is null
            || checkpoint.Version != ProgressCheckpoint.CurrentVersion
            || !string.Equals(checkpoint.Algorithm, ProgressCheckpoint.CurrentAlgorithm, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Fingerprint, fingerprint, StringComparison.Ordinal)
            || checkpoint.Answered < 0
            || checkpoint.States is null
            || checkpoint.Minutes is null)
        {
            return null;
        }

        try
        {
            if (DecodeMinutes(checkpoint.Minutes) is not { } minutes)
            {
                return null;
            }

            Dictionary<QuestionId, ReviewState> states = new(checkpoint.States.Count);
            foreach (CheckpointState state in checkpoint.States)
            {
                if (!QuestionId.TryCreate(state.Question, out QuestionId id) || !GradeKeys.TryParse(state.Grade, out Grade grade))
                {
                    return null;
                }

                states[id] = new ReviewState(
                    new DateTimeOffset(state.Due, TimeSpan.Zero),
                    state.Interval,
                    state.Ease,
                    state.Repetitions,
                    state.Lapses,
                    grade,
                    new DateTimeOffset(state.Answered, TimeSpan.Zero));
            }

            return new LocalProgress(states, minutes, checkpoint.Answered, fingerprint);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>When each of the <paramref name="live"/> questions is due; questions never answered are absent.</summary>
    public IReadOnlyDictionary<QuestionId, DateTimeOffset> DueAtById(IReadOnlySet<QuestionId> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        Dictionary<QuestionId, DateTimeOffset> due = new(Math.Min(live.Count, _states.Count));
        foreach ((QuestionId id, ReviewState state) in _states)
        {
            if (live.Contains(id))
            {
                due[id] = state.DueAt;
            }
        }

        return due;
    }

    /// <summary>Every state as question id and due time; <see cref="Trainer.Domain.Sessions.ReviewOrder.Pick"/> filters by the live set itself.</summary>
    public IEnumerable<(QuestionId Id, DateTimeOffset DueAt)> DueStates() => _states.Select(s => (s.Key, s.Value.DueAt));

    /// <summary>
    /// Consecutive local answer days ending today or yesterday, over every answer of every stack. A day is the date at
    /// <paramref name="utcOffset"/> (the device's offset), so an answer at 23:30 and one at 00:30 are two days.
    /// </summary>
    public int StreakDays(DateTimeOffset now, TimeSpan utcOffset)
    {
        StreakAccumulator streak = new(DateOnly.FromDateTime(now.ToOffset(utcOffset).DateTime));
        for (int i = _answerMinutes.Length - 1; i >= 0; i--)
        {
            DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(_answerMinutes[i] * 60L).ToOffset(utcOffset);
            if (!streak.Add(DateOnly.FromDateTime(at.DateTime)))
            {
                break;
            }
        }

        return streak.Days;
    }

    /// <summary>The sidebar and session bar view of the progress for the questions <paramref name="index"/> lists.</summary>
    public IslandProgress Summarize(StaticIndexDto index, DateTimeOffset now, TimeSpan utcOffset, IReadOnlyList<TopicDto>? topicOrder = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ProfileDto profile = LocalProfileBuilder.Build(index, this, [], now, utcOffset, topicOrder);
        return new IslandProgress(profile.StreakDays, profile.DueNow, profile.Topics);
    }

    // Ordered by time, then by id (ordinal): the order every replay and every appended batch is applied in.
    private static List<(DateTimeOffset At, string Id, QuestionId Question, Grade Grade)> ValidAnswers(IReadOnlyList<AnswerDto> log)
    {
        List<(DateTimeOffset At, string Id, QuestionId Question, Grade Grade)> answers = new(log.Count);
        foreach (AnswerDto answer in log)
        {
            if (answer.AnsweredAt is { } at
                && at >= EarliestAnswer
                && at <= LatestAnswer
                && QuestionId.TryCreate(answer.QuestionId, out QuestionId question)
                && GradeKeys.TryParse(answer.Grade, out Grade grade))
            {
                answers.Add((at, answer.ClientAnswerId, question, grade));
            }
        }

        answers.Sort(static (a, b) =>
        {
            int byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
        });
        return answers;
    }

    // The minutes are ascending and mostly close together, so they are stored as the first value and the gaps, each as a
    // zigzag varint, in hex: a log of ten thousand answers is a few tens of kilobytes, and one string is parsed in one call
    // where thousands of JSON numbers would be parsed one by one.
    private static string EncodeMinutes(int[] minutes)
    {
        List<byte> bytes = new(minutes.Length * 2);
        long previous = 0;
        foreach (int minute in minutes)
        {
            long delta = minute - previous;
            ulong zigzag = (ulong)((delta << 1) ^ (delta >> 63));
            while (zigzag >= 0x80)
            {
                bytes.Add((byte)(zigzag | 0x80));
                zigzag >>= 7;
            }

            bytes.Add((byte)zigzag);
            previous = minute;
        }

        return Convert.ToHexString([.. bytes]);
    }

    // Null for anything EncodeMinutes does not write: not hex, a truncated or oversized number, a gap that is not positive.
    private static int[]? DecodeMinutes(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes = Convert.FromHexString(hex);
        List<int> minutes = new(bytes.Length);
        long previous = 0;
        int position = 0;
        while (position < bytes.Length)
        {
            ulong zigzag = 0;
            int shift = 0;
            byte next;
            do
            {
                if (position >= bytes.Length || shift > 28)
                {
                    return null;
                }

                next = bytes[position++];
                zigzag |= (ulong)(next & 0x7F) << shift;
                shift += 7;
            }
            while ((next & 0x80) != 0);

            long delta = (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
            long minute = previous + delta;
            if (minute is < int.MinValue or > int.MaxValue || (minutes.Count > 0 && delta < 1))
            {
                return null;
            }

            minutes.Add((int)minute);
            previous = minute;
        }

        return [.. minutes];
    }

    private static int MinuteOf(DateTimeOffset at)
    {
        // Floored, so a time before 1970 still lands in the minute that holds it.
        long seconds = at.ToUnixTimeSeconds();
        return (int)(seconds / 60 - (seconds % 60 < 0 ? 1 : 0));
    }

    // The answers are ascending, so equal minutes are adjacent.
    private static int[] MinutesOf(IEnumerable<DateTimeOffset> ascending)
    {
        List<int> minutes = [];
        foreach (DateTimeOffset at in ascending)
        {
            int minute = MinuteOf(at);
            if (minutes.Count == 0 || minutes[^1] != minute)
            {
                minutes.Add(minute);
            }
        }

        return [.. minutes];
    }
}
