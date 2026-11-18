# ADR 0028: The static edition stores a checkpoint of its replayed progress next to the answer log

## Status

Accepted as the outcome of the fix round of Task 17 (controller ruling: keep the answer log as the source of truth and add a
cached aggregate). It amends the design's section 5.6, "Nothing derived is stored" and "Why no snapshot", for the reasons below.

## Context

The static edition's progress is the whole anonymous answer log (`dit.outbox.v1.anon`) replayed through the scheduler on
every page load (section 5.6). With ten thousand answers the first measurement on the emulated phone
(`docs/performance.md`) put "usable" at 23.2 s cold and 17.8 s warm, and most of that was not the replay:

| Phase of one load, 10 000 answers (emulated phone, three Release runs) | Time |
|---|---|
| Read the log as a string and parse it into a document (`StoredDocument.Parse`) | 1.4 to 2.3 s |
| Turn each of the entries into an `AnswerDto` and validate it | 3.6 to 5.4 s |
| Collect every entry id | 0.6 to 0.8 s |
| Replay through the scheduler | 1.0 to 1.3 s |

The runtime is interpreted, so work that is per entry (parsing, allocating, validating) costs seconds, while the same
work in the browser's JavaScript engine (`outbox.js` parses the whole log in 10 to 20 ms) costs nothing. Each answer also
dropped the replay and made the next read replay again.

## Decision

1. **The log stays the only source of truth.** A `ProgressCheckpoint` (`dit.progress.v1.anon`) is derived data: the review
   state of every answered question, the distinct answer minutes the streak needs (stored as gaps in a hex string) and the
   answer count. Deleting it, or any change that makes it not match the log, only costs one replay.
2. **It is trusted only for the log state it was written for.** `outbox.js` computes a fingerprint of a log natively (entry
   count and a hash of the last 64 entries' id, grade and time) and reports it for a key without the log crossing into .NET
   (`ditStore.fingerprint`); an append also reports the fingerprint before and after it and how many entries were new. A
   checkpoint is used when its fingerprint is the log's, its version and algorithm name are this build's, and it parses;
   anything else is ignored. A log changed in the middle by hand without changing the count or the tail is not noticed:
   the log is append-only in the static edition (it is never trimmed or moved), which is why the tail is enough.
3. **A load takes the cheapest exact route.** `LocalProgressCache` reads the fingerprint, then the checkpoint (a few
   milliseconds, whatever the length of the log); otherwise it replays the whole log, taking the fingerprint on both sides of
   the read and storing the checkpoint only when no other tab wrote in between. Answers that live only in memory (storage
   refused them) always force a replay: no stored checkpoint can contain them.
4. **An answer this tab writes is applied to the memo, not replayed.** `OutboxStore.Appended` hands the cache the answers and
   the script's result. The cache extends its progress only when the write started from exactly the log state the memo
   describes (`Previous` equals the memo's fingerprint), added exactly those answers, and each answer is later than the last
   one of its question and not in an earlier minute than the last of the log (`LocalProgress.TryAppend`: the cases where
   applying on top is the same as replaying). Any other case, such as another tab writing first or a backdated answer,
   drops the memo and the next read loads again. The checkpoint is then written again after a short wait
   (`LocalProgressCache.DefaultCheckpointDelay`, one second), so the answer's own screen is drawn first and an answer streak
   costs one write. A page left inside the wait leaves a checkpoint that no longer matches; the next load replays once.
5. **The algorithm has a name that tests pin.** `ProgressCheckpoint.CurrentAlgorithm` names the scheduler's rules and the
   replay order. A test replays a fixed log and compares a hash of the states with a literal, so a change to the scheduler
   that forgets to rename the algorithm fails the build instead of serving states of the old rules to existing visitors.
6. **The append hot path stops walking the whole log twice.** `outbox.js` checks the depth of the entries it adds rather
   than of the whole document it has just classified, and its depth check allocates nothing.

## Consequences

- A visitor's first load after this change, and the loads that follow a write the checkpoint missed, still replay the whole
  log once (`docs/performance.md` records the cost); every other load reads the checkpoint.
- `dit.progress.v1.anon` takes storage next to the log (about 75 KB for ten thousand answers). Clearing it is always safe,
  and the app does it: an answer-log append refused for quota deletes the checkpoint and is tried once more, so derived
  data never costs an answer its place in the log. A checkpoint still waiting to be written when another tab changes the
  log is dropped, not written, so a stale one never replaces a fresher one.
- The quota notice and the 5 MB limit of section 5.6 are unchanged; the checkpoint is not part of the answer log and is
  never sent anywhere.
- The server edition is untouched: the checkpoint exists only behind `LocalProgressCache`, which only the static backend uses.

## Amendment: answer times in the replay (final review)

- **The replay applies an absolute sanity window, not ADR 0005's clock-relative limits.** ADR 0005 judges a reported time
  against the clock when an answer is recorded (not older than a year, at most five minutes ahead). A replay judged that way
  would depend on the current time, which breaks the rule that a checkpoint is keyed by the log alone, and it would age
  answers out of the progress after a year. `LocalProgress` therefore counts an answer only when its time lies between
  `EarliestAnswer` (2000-01-01) and `LatestAnswer` (2100-01-01); anything else is "not an answer", like an unknown grade. The
  window also keeps the scheduler's `AddDays` and the minute arithmetic far from overflow, so one stored `9999-12-31` entry
  (a hand edit, an extension) can no longer fail every Practice, Review and Profile page.
- **A device clock that was ahead costs one question, not the whole log.** An answer in the future but inside the window still
  counts, as before. `TryAppend` used to refuse every answer in an earlier minute than the last one of the log, so after one
  future-dated answer every later answer fell back to a full replay. It now inserts the answer's minute in order. Only an
  answer to the same question as the future-dated one still needs a replay, because a replay orders it before that answer.
- **A far-future answer locks its question out until the log is reset (accepted).** One answer dated far ahead (up to 2100)
  makes every later real answer to that question older than it, so the scheduler ignores those answers and the question is
  not due again until the log is reset (profile reset, clearing site data). Accepted: it needs a broken device clock, and a
  narrower window would need the current time, which would make the replay depend on more than the log.
- **A finished mock outranks the checkpoint too.** `LocalMockHistoryStore` calls the same make-room hook as the outbox: a
  `historyAdd` refused for quota deletes the checkpoint and is tried once more, so the mock is not kept in memory only while
  derived data holds the space.
