# ADR 0002: The session cookie is named through cookie options

## Status

Accepted.

## Context

Spec section 8.4 makes the output cache skip every request that carries the
session cookie, and Plan 1 already keys that check on `AuthCookieNames.Session`
(`.dit.session`). Sign-in comes from `Telegram.OpenIdConnect.AspNetCore`, which
registers the cookie scheme itself. `TelegramOidcOptions` exposes the scheme
names, paths and lifetime, but no cookie name, so the cookie would otherwise get
ASP.NET Core's default name (`.AspNetCore.TelegramCookie`). The library belongs
to the owner and is consumed as a published package; it is not changed for this
app.

## Decision

`AddTrainerAuth` registers
`Configure<CookieAuthenticationOptions>(TelegramAuth.CookieScheme, o => o.Cookie.Name = AuthCookieNames.Session)`
after `AddTelegramOidc`. Named options run their configuration actions in
registration order, so this one runs after the library's and only the name
changes; the library's `HttpOnly`, `SameSite=Lax`, `Secure` (always) and sliding
expiration stay. The same registration attaches `TrainerCookieEvents` through
`EventsType`.

## Consequences

- One constant names the cookie for the auth handler, the output cache bypass
  and the tests (`SessionCookieReader`).
- The name does not reveal the framework (`.AspNetCore.*`) and is short.
- If a later library version adds a cookie-name option, this registration can
  move into the `AddTelegramOidc` callback; until then a library update must
  keep running its cookie configuration before ours, which the sign-in tests
  check by reading the issued `.dit.session` cookie.
