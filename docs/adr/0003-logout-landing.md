# ADR 0003: Signing out lands on the sign-in page

## Status

Accepted.

## Context

The spec asks for a logout control in the static SSR layout (section 8.3) but
does not say where the user goes afterwards. Sign-out is served by
`Telegram.OpenIdConnect.AspNetCore`: `POST /signin-telegram/logout` validates the
antiforgery token, signs out of the cookie scheme and always redirects to the
configured `LoginPath`. There is no return-URL option, and the library is not
changed for this app.

## Decision

Keep the library's behaviour. `LoginPath` is the culture-neutral `/signin`, which
redirects to `/{culture}/signin` with the culture from the `lang` cookie or
`Accept-Language`. A signed-out user therefore lands on the sign-in page in their
language, which confirms the sign-out and offers to sign in again.

## Consequences

- No custom logout endpoint and no second antiforgery path to maintain.
- The user does not return to the page they were on. That page may have been a
  signed-in page (profile, practice) that would only redirect to sign-in anyway.
- The sign-in page must work for an anonymous visitor without a return URL; it
  then returns to the culture home after the next sign-in.
