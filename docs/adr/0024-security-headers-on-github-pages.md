# ADR 0024: Security settings on GitHub Pages are meta elements

## Status

Accepted by the owner on 2026-11-17 as part of the static edition design (section 5.8 of
`docs/superpowers/specs/2026-11-17-static-edition-design.md`); this record states what is lost and how the rest is kept.

## Context

The server edition sends a Content-Security-Policy, `X-Content-Type-Options` and `Referrer-Policy` on every response and
adds a per-request nonce for the framework import map (ADR 0012). GitHub Pages sets no custom response headers, and a
static file cannot carry a per-request nonce.

## Decision

1. Every static page starts its `<head>` with `<meta http-equiv="Content-Security-Policy">`, before any inline script,
   followed by `<meta name="referrer" content="strict-origin-when-cross-origin">`. `StaticHeadMeta` renders them from
   `StaticCsp`; the root chooser, `404.html` and the redirect pages use the same policy, and `<meta charset>` follows
   at once so the encoding is still declared within the first 1024 bytes.
2. Inline scripts are allowed by SHA-256, not by nonce: the theme script, the recovery script (`RecoveryScript`) and,
   on island pages, the import map, which is written as text so its hash is known (`App.razor` renders
   `ImportMapDefinition` itself in the static edition). A new inline script on a static page must have its hash added
   in `StaticCsp`, or the browser refuses it; `StaticCspTests` parses every exported-like page and checks this.
   Data blocks such as JSON-LD are not scripts to a browser and need no hash.
3. Island pages add `'wasm-unsafe-eval'`, the import map hash and `base-uri 'self'`; every other page has
   `base-uri 'none'`. GoatCounter's script is self-hosted, so only the site's own
   `https://<code>.goatcounter.com` is added, to `connect-src`, and only when `App:Analytics:GoatCounterCode` is set
   (ADR 0025).
4. In the static edition the application sends none of these headers (`SecurityHeadersMiddleware` steps aside), so a
   local static run behaves like Pages and a test cannot pass by relying on a header that Pages will not send.

## Consequences

Lost on Pages, by design:

- `frame-ancestors`. A meta policy cannot carry it and Pages sends no `X-Frame-Options`, so the site can be framed. There
  is no cookie, no posting form, no token and no account, so framing gives an attacker nothing to click on.
- `X-Content-Type-Options: nosniff`. Pages serves the right media types for the files the export writes.
- CSP reporting (`report-uri` and `sandbox` are ignored in a meta policy too).
- HSTS configuration. github.io is on the browsers' HSTS preload list, so HTTPS is enforced anyway.

The policy is the same for every visitor and changes only with a deployment, so it needs no operation after the first
setup. A page of an older deployment keeps the policy it was exported with; the recovery script (design 5.13) covers
the case where it refers to a file that no longer exists.
