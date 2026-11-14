# ADR 0005: Answer timestamps: client-stamped, judged on the server clock with a 5-minute tolerance

## Status

Accepted (approved by the owner). It changes spec 8.8, which says "`answeredAt` not in the future and not
older than 1 year".

## Context

Answers reach the server through the client outbox, sometimes hours or days after they were given (offline
sessions, closed tabs, retries). The schedule must use the time an answer was given, not the time it arrived:
an answer given just before a question became due and delivered after it must still count as "not yet due".
So `answeredAt` comes from the client.

Device clocks are wrong in both directions. The client therefore calibrates an offset to the server clock from
the `Date` header of its `/api` responses and stamps `answeredAt` with the corrected time at the moment of
answering; the value is never recomputed, so retries and later clock changes cannot redate an answer. The
`Date` header has a resolution of one second, so a correct answer can still look up to about a second ahead of
the server. Rejecting every answer that is ahead, as the spec text says, would drop real answers.

`answeredAt` is client-controlled and arbitrary values must not break the server: `DateTimeOffset.MinValue`
and `MaxValue` are valid JSON dates.

## Decision

`AnswerTimePolicy` (Application) judges a reported time against the server's now:

| Reported `answeredAt` | Verdict | Effect |
|---|---|---|
| older than now − 365 days | `TooOld` | rejected as `answered-too-old`, never stored |
| more than now + 5 minutes | `InFuture` | rejected as `answered-in-future`, never stored |
| after now, up to now + 5 minutes | `Clamp` | stored as the server's now |
| otherwise | `Accept` | stored as reported |

- Only the server's clock is shifted (`now − MaxAge`, `now + FutureTolerance`); the client value is only
  compared, so extreme dates cannot overflow.
- The schedule is computed from the stored (`Effective`) `answeredAt`.
- The policy applies only to new client ids. An id that is already stored is acknowledged first, without
  judging the time again, so a retry after a year or after the server clock moved is still accepted and the
  stored time never changes. The same id with a different payload keeps the first write.
- Mock summaries use the same policy for `startedAt` and `finishedAt`; a rejected time makes the summary invalid.
- A user can still backdate an answer within the year. This is accepted, like the self-reported grade
  (spec 11.5): the data only affects that user's own schedule.

## Consequences

- No real answer is lost to sub-second clock error, and no stored answer lies in the future.
- Rejected answers are acknowledged as rejected, so the client removes them from its outbox instead of retrying
  them forever.
- The 5-minute tolerance lets a client whose calibration is a few minutes off place answers up to 5 minutes
  later than they were given; the schedule granularity is days, so this does not matter.
- Several answers to one question that are all clamped in one batch share the stored time `now`, and the scheduler
  ignores an answer that is not newer than the last applied one, so only the first of them counts. They are applied
  in the order the client reported them, then by client id, so "the one given first" is well defined. A client that
  is minutes ahead and answers one question twice within that window keeps the first grade; this is rare and
  accepted.
