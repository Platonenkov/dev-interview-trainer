# ADR 0009: Island pages start Blazor with enhanced navigation off

## Status

Accepted (approved by the owner).

## Context

Practice, Mock, Review and the profile are WebAssembly islands on noindex pages;
every other page is static SSR without `blazor.web.js` (spec 8.2). Spec 8.2
allows enhanced navigation into an island page as long as a prerendered island
keeps its state with `[PersistentState(AllowUpdates = true)]`, so that a runtime
that is already running refreshes the state instead of fetching it again.

The islands never prerender: the server renders a fixed-size skeleton and the
island loads its data once, on the client. With enhanced navigation on:

- a public page loaded from an island page would be patched into the island
  document, so the runtime would keep running on a page that must not have one,
  and its later requests would come from a page the tests treat as runtime-free;
- entering an island page from another island page would reuse the running
  runtime, and whether the island starts once or twice, and fetches its session
  once or twice, would depend on where the visitor came from;
- the theme attribute set by the inline head script is dropped by the DOM merge
  and has to be restored on `enhancedload`.

Public pages do not load `blazor.web.js` at all, so navigation from a public page
into an island is already a full page load. The open question is the other
direction and island-to-island.

## Decision

- `islands-start.js` starts Blazor with
  `Blazor.start({ ssr: { disableDomPreservation: true }, webAssembly: { applicationCulture } })`:
  enhanced navigation and enhanced form handling are off. Every navigation from
  an island page, to a public page or to another island page, is a full page
  load. A WebAssembly runtime never outlives its page, and every island document
  starts exactly one runtime and fetches its session once.
- Links that lead into island pages from public pages carry
  `data-enhance-nav="false"` anyway, as does the language switcher (spec 8.3), so
  the boundary does not depend on which side has the runtime.
- `islands-start.js` still registers `enhancedload` to re-apply the theme, as a
  safety net should enhanced navigation ever be turned on.
- Island pages carry `<base href="/">`: the runtime, `<ResourcePreloader />` and
  the JavaScript initializers resolve `_framework/...` against the document base,
  and island pages live at `/{culture}/{stack}/...`. Their CSP therefore has
  `base-uri 'self'` (public pages keep `'none'`) next to `'wasm-unsafe-eval'`.
  Under that base a bare `#fragment` link would leave the page, so fragment links
  on island pages are written with the page's own path (the skip link does this).
- The navigation tests prove document identity, not `navigation.type`: before
  each click the test sets a random `window.__ditDocId`, and the arriving page
  must not have it.

## Consequences

- Moving between island pages costs a full page load and a runtime start; with a
  warm cache that is the start-up time measured in `docs/performance.md`
  (Task 21), within the spec 8.2 budget of 2 s.
- `[PersistentState]` is not needed for islands; nothing prerenders.
- No island state survives a navigation. Anything that must survive (answers,
  filters) is in `localStorage` or on the server, which spec 8.8 requires anyway.
- A future island that wants enhanced navigation must revisit this decision and
  bring back prerendering with persistent state.

## Note, 2026-11-17

The server edition keeps `<base href="/">`; an edition served under a path base (ADR 0022) writes that path base in the
same element.
