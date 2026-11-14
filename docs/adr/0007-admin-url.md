# ADR 0007: The admin page lives at /{culture}/admin

## Status

Accepted (approved by the owner).

## Context

The spec gives the Admin role a health page, the content import status and a user
block flag (section 4) but lists no URL for it in the route table (section 8.3).
Every page of the app is served under a culture prefix, and the culture prefix is
what selects the UI language: the localisation middleware reads it from the route,
so a page outside it would need its own language rule.

The page shows personal data (Telegram ids, names, usernames) and carries POST
forms, so it must never be cached, compressed with a token in the body, or
indexed.

## Decision

The admin page is `/{culture}/admin`, a routable component with
`[Authorize(Policy = "Admin")]`.

- It follows the culture prefix like the sign-in and about pages, so RU and EN
  strings come from the usual resources and the language switcher works.
- Authorisation runs before the page, so a visitor who is not an admin never sees
  anything that depends on the query: an anonymous request is redirected to
  `/signin?ReturnUrl=...`, a signed-in non-admin to `/signin?denied=1&ReturnUrl=...`.
  Whether a user exists cannot be probed through the page.
- It has no `[OutputCache]`, the public cache policies do not apply to it, and the
  signed-in HTML rules apply as on every authenticated page: `private, no-store`
  and no response compression (the page renders antiforgery tokens).
- `<meta name="robots" content="noindex">` keeps it out of search results. It is
  deliberately not listed in `robots.txt`, which would advertise the URL to anyone
  who reads that file.
- Every block and unblock action is a `POST` form with an antiforgery token; a
  request without a valid token is answered 400 before the action runs.

## Consequences

- The URL is one more entry in the route table in `docs/architecture.md`.
- The page is not linked from the site header. An admin opens it by address; a link
  for the Admin role can be added later without a routing change.
- The route `/{culture}/admin` takes precedence over `/{culture}/{stack}`, so a stack
  keyed `admin` would be unreachable, as one keyed `about` or `signin` already is.

## Notes added in review

- The search text (a Telegram id, name or username) is sent as the `q` query
  parameter of a GET form. The application never logs query strings (no
  `HttpLogging`), and the page's referrer policy keeps the path and query out of
  cross-origin requests. A POST would make the result page impossible to bookmark
  and to page through, and the text is visible only to admins, so GET is kept. The
  reverse proxy (Plan 3) must not write query strings of `/{culture}/admin` to its
  access log; that is a deployment requirement, not application code.
- The security stamp is re-checked against the database at most every 5 minutes, so
  during a database outage the page can still be refused at authentication time.
  Once authenticated, the page renders the "Database unreachable" state instead of
  failing.
- A configured admin row shows a "Configured admin" label instead of a Block
  button; `IUserAccountService` still refuses the action for a forged request. A
  configured admin who was blocked before being configured keeps an Unblock button.
- The anonymous redirect to `/signin?ReturnUrl=...` is a relative URL.
