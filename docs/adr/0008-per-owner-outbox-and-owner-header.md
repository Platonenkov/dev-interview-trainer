# ADR 0008: A per-owner outbox and the X-Outbox-Owner header

## Status

Accepted (approved by the owner), except for one part that is **awaiting the
owner's confirmation**: the behaviour of reads that carry the header (409
`owner-mismatch` when it is not who is signed in, and 401 `sign-in-required`
when it names a user and nobody is signed in) was added after the approval.
It is implemented and tested; if the owner rejects it, the reads revert to
ignoring the header.

## Context

Answers are written to an outbox in `localStorage` first and flushed to
`POST /api/answers/batch` later (spec 8.8). A browser profile can be shared, and
an account can change under a page that is still open: tab 1 holds answers given
while signed in as A, tab 2 signs out and signs in as B, and tab 1 flushes. The
auth cookie is per browser, not per tab, so the flush arrives with B's session
and the server would store A's answers under B. Nothing in the batch itself says
whose answers they are, and the antiforgery token does not help: tab 1 refetches a
token after the 400 it gets and that token is B's.

The same applies to the mock summary written with `PUT /api/mock-sessions/{id}`.

The session reads (`POST /api/sessions/practice`, `/mock`, `/review`) are `POST`
only because an anonymous "new questions" filter carries up to 1000 excluded ids,
too many for a query string. They change no state.

## Decision

- The client keeps one outbox per owner, in its own `localStorage` key: one for
  anonymous answers and one per Telegram user id for answers given while signed
  in. Each key holds exactly the spec 8.8 outbox document; only the number of keys
  changes. How anonymous answers are imported after the first sign-in is the
  client's flush logic (Task 14).
- Every write (`POST /api/answers/batch`, `PUT /api/mock-sessions/{id}`) carries
  `X-Outbox-Owner: <Telegram user id>` of the outbox it flushes.
  `OwnerCheckFilter` compares it with the `telegram_user_id` claim of the session.
  A missing, malformed (anything but one positive decimal integer) or different
  owner is answered `409` with `ApiErrorDto("owner-mismatch")`, before the body
  reaches any service, so nothing is stored. The check runs after the antiforgery
  check, so a request that fails both is reported as `antiforgery`.
- Every write also carries `X-Client-Utc-Offset`, the device offset in minutes.
  An integer within ±840 (`IAnswerRecorder.MaxUtcOffsetMinutes`) is stored on the
  user and used only to count streak days in local time; anything else is ignored
  and never fails the write.
- On 409 the client keeps the entries, stops flushing and reports the account
  mismatch; it never rewrites the owner of an entry.
- The session reads take no antiforgery token. They are protected by being
  read-only and by the browser's same-origin rules: binding accepts only
  `application/json` (anything else, including a missing content type, is 415),
  which a cross-site page can send only after a CORS preflight, and the app has no
  CORS policy, so the preflight is refused and the response is never readable
  cross-origin.

## Consequences

- Answers given under one account are never stored under another, whatever the
  tabs do; tests cover the 409 with nothing stored (Task 11), the client keeping
  its entries on 409 (Task 14) and two tabs in a real browser (Task 16).
- The Telegram user id stays in `localStorage` key names after sign-out on a shared
  browser. Accepted: the id is not a secret.
- The header is not a security boundary on its own: the session decides who the
  user is, the header only stops a write the client did not mean for that user.
- Adding a CORS policy later, or accepting a simple content type on a session
  read, would reopen cross-site requests to those endpoints; either change needs
  antiforgery on them first.
- Reads may carry the header too (a Telegram user id, or `anon`): session reads
  and the profile answer 409 `owner-mismatch` when it is not who is signed in
  now, so a stale tab shows the account notice instead of another account's
  data. When the header names a user but nobody is signed in (the cookie
  expired), the answer is 401 `sign-in-required`, so the island shows "signed
  out" rather than "account changed". A read without the header is served as
  before; the islands always send it.
