namespace Trainer.Application.Mock;

public enum MockSaveResult
{
    /// <summary>The session did not exist and was stored.</summary>
    Created,

    /// <summary>The caller's session existed and was replaced.</summary>
    Updated,

    /// <summary>The id belongs to another user; reported as missing, so the caller learns nothing about that session's content or owner (a free id still answers 204, and ULIDs carry 80 random bits, so guessing one is impractical).</summary>
    NotFound,

    /// <summary>A time is rejected by the answer time policy, or the session finished before it started.</summary>
    Invalid,
}
