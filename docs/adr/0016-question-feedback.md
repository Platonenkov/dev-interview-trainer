# ADR 0016: Question feedback is two plain tables, a per-user report quota, and no outbox

## Status

Accepted. A design decision of Plan 3, taken by the designer inside the scope of the
"Question feedback" paragraph of spec section 4; no spec text changes.

## Context

The spec lets a signed-in user like or dislike a question and report it with a reason
from a fixed list plus an optional comment. It fixes one changeable rating per user and
question, rate-limited reports and an Admin list, and leaves the storage, the limit, the
offline behaviour and the places that show the controls open.

## Decision

- **Storage.** `question_ratings` holds one row per user and question (composite key);
  a rating is a single `INSERT ... ON CONFLICT (user_id, question_id) DO UPDATE`, so a
  double click or two tabs store one row, and clearing a rating deletes the row.
  `question_reports` holds one row per report with a ULID key, the culture of the page
  it was filed from, the reason, the optional comment (at most 1000 characters after
  normalising), and `resolved_at` and `resolved_by` once an admin closes it. Resolved
  reports stay as history. Both tables are new, so the migration only adds tables.
- **Quota.** `App:Feedback:ReportsPerDay` (default 10, must be positive) limits new
  reports per user in a rolling 24 hours. A report takes a per-user advisory lock before
  it counts, so parallel requests cannot pass the limit. An open report of the same user
  for the same question and reason is edited instead of duplicated (its comment and
  culture change); that does not count against the quota. After an admin resolves it,
  the same reason files a new report. A rejected report tells the caller when the oldest
  counted report leaves the window.
- **Availability.** A retired question, or a question of a retired topic or stack, takes
  no new rating or report, the same rule as answers. Clearing a rating always works, so a
  rating given before a question was retired stays removable. Retired questions are left
  out of the most-disliked list.
- **No outbox.** Feedback is not queued in the browser outbox. A failed write shows an
  error and the user can repeat it. An offline rating is not worth a second sync
  protocol next to the answer outbox, and a rating nobody sees land is a poor
  experience anyway.
- **Placement.** The controls appear on the question page (Task 5) and in Practice and
  Review after the user has answered (Task 4). They are not in Mock, whose summary links
  to the question pages. Retired questions get no controls. Anonymous visitors see none.
- **Privacy.** Comments are user text: they are stored and shown to admins, never logged.

## Consequences

- The cost of a rating is one statement and of a report one short transaction.
- The migration is expand-only, so the previous release keeps working on the migrated
  schema; the new tables are ignored by it.
- Deleting a user deletes their ratings and reports; deleting an admin account keeps the
  reports they resolved and clears `resolved_by`.
- Reports and totals are read through `IQuestionFeedbackQueries` for the admin page;
  nothing aggregates ratings into the public pages.
