# ADR 0010: The profile lives at /{culture}/{stack}/profile

## Status

Accepted (approved by the owner).

## Context

Spec 4 gives signed-in users "statistics per topic and level, streak", and spec
8.2 lists the profile among the island pages next to Practice, Mock and Review,
but the route table in spec 8.3 names no URL for it.

Statistics are per stack: topics belong to a stack, `GET /api/profile` takes a
stack, and the review queue and the mock history are counted per stack. The
other island pages live at `/{culture}/{stack}/{mode}`, where
`CultureAndStackMiddleware` already answers 404 for an unsupported culture or an
unknown stack, and `robots.txt` already disallows `/profile` next to the mode
segments.

## Decision

The profile is `/{culture}/{stack}/profile`, an island page
(`[InteractiveIslandPage]`) with `[Authorize]`.

- The culture and stack checks of the mode pages apply unchanged; the culture
  segment is constrained to two letters like theirs.
- An anonymous request is redirected to `/signin?ReturnUrl=...` and comes back
  after the sign-in.
- Like every island page it is `noindex`, never output-cached and never
  compressed; as a signed-in page it is `private, no-store`.
- The account name in the header links to the profile of the default stack
  (`App:DefaultStack`), and so does the anonymous-import notice of signed-in
  public pages (`.../profile#import`).

## Consequences

- A second stack gets its own profile URL without a new route; the header link
  points to the default stack's until a stack switcher exists.
- Spec 8.3 gains one row; nothing else in the spec changes.
