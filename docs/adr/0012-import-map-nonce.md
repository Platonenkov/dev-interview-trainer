# ADR 0012: Island pages render the framework import map under a per-request CSP nonce

## Status

Accepted. Changes the Plan 2 task text "No `<ImportMap />`" (Task 12); no spec
text changes.

## Context

Island pages preload the WebAssembly runtime with `<ResourcePreloader />`, which
names the fingerprinted file (`_framework/dotnet.{fingerprint}.js`, cached as
`immutable`). `blazor.web.js` then imports the runtime by its plain name,
`./_framework/dotnet.js`. Without an import map nothing translates that name, so
the published build downloaded `dotnet.js` twice on every cold load (once
fingerprinted for the preload, once unfingerprinted for the import, served
`no-cache` and revalidated on every later visit), and the runtime files that
`runtime-preload.js` prefetches from public pages were not the ones the runtime
asked for.

The framework's fix is `<ImportMap />`, an inline
`<script type="importmap">` whose content maps each plain name to its
fingerprinted file. The CSP allows no `'unsafe-inline'`, and the map changes with
every build, so a fixed hash (as for the theme script) cannot allow it either.

## Decision

- Island pages (`[InteractiveIslandPage]`) render `<ImportMap />` in `<head>` with
  a `nonce` attribute, and their CSP adds `'nonce-{value}'` to `script-src`.
- The nonce is `CspNonce`, a scoped service: 18 random bytes from
  `RandomNumberGenerator` (24 base64url characters: no `+`, which the HTML
  encoder would write as an entity), created on first use and the same
  for the page and its header. `SecurityHeadersMiddleware` reads it when the
  response starts.
- Public pages are unchanged: no import map, no nonce, no runtime script, the same
  static CSP. Island pages are never output-cached or stored by browsers
  (`no-store`), so a nonce is never reused across responses.
- The published-build suite (`tests/Trainer.Web.Release`) proves the effect: every
  `_framework/` file is requested exactly once per cold load, under its
  fingerprinted name, the prefetched files are the ones the runtime requests, and a
  warm visit loads all of them from the browser cache.

## Consequences

- The import map lists every fingerprinted JavaScript asset of the app (a few
  kilobytes of HTML per island page).
- A nonce only helps a script the server itself rendered; an injected
  `<script>` without it is still blocked, so the CSP stays free of
  `'unsafe-inline'`.
- Should an island page ever be cached, the cached nonce would no longer match a
  fresh header; the "never cached" rule for island pages now also protects this.
