using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// The static edition's replayed progress written next to the answer log (<c>dit.progress.v1.anon</c>), so a page load does
/// not replay thousands of answers: reading a log of ten thousand entries into .NET costs seconds on a phone, reading this
/// costs milliseconds. It is derived data. The answer log stays the only source of truth, and a checkpoint is used only
/// while its <see cref="Fingerprint"/> is the one <c>outbox.js</c> reports for the log; any other checkpoint is ignored and
/// rebuilt by a replay.
/// </summary>
/// <param name="Version">The shape of this document (<see cref="CurrentVersion"/>).</param>
/// <param name="Algorithm">What produced the states (<see cref="CurrentAlgorithm"/>): the scheduler's rules and the replay order.</param>
/// <param name="Fingerprint">The log state the progress was computed from.</param>
/// <param name="Answered">How many valid answers the log held.</param>
/// <param name="Minutes">The distinct Unix minutes with an answer, ascending, as hex of zigzag varints: the first minute, then
/// each gap to the previous one (a gap is always positive). Answers cluster in sessions, so most gaps take one byte; one
/// string is decoded in one pass where an array of ten thousand JSON numbers would be parsed one by one.</param>
/// <param name="States">The review state of every answered question.</param>
public sealed record ProgressCheckpoint(
    int Version,
    string Algorithm,
    string Fingerprint,
    int Answered,
    string Minutes,
    IReadOnlyList<CheckpointState> States)
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Names the behaviour behind the states. Change it with the scheduler's rules, <see cref="LocalProgress.Replay"/> or the
    /// checkpoint's meaning: every checkpoint written under another name is then ignored. A test pins the scheduler's
    /// behaviour to this name, so a change that forgets to bump it fails the build.
    /// </summary>
    public const string CurrentAlgorithm = "sm2-replay-1";
}

/// <summary>One question's <see cref="Trainer.Domain.Scheduling.ReviewState"/>; times are UTC ticks and exact.</summary>
public sealed record CheckpointState(string Question, long Due, int Interval, decimal Ease, int Repetitions, int Lapses, string Grade, long Answered);

/// <summary>Serializer metadata for the checkpoint (the client is trimmed).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProgressCheckpoint))]
internal sealed partial class ProgressCheckpointJsonContext : JsonSerializerContext;
