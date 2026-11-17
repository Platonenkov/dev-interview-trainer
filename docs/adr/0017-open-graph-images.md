# ADR 0017: Open Graph images are rendered lazily, cached on a volume and addressed by fingerprint

## Status

Accepted. A design decision of Plan 3, taken by the designer inside the scope of the SEO
section of the spec; no spec text changes.

## Context

A link to a question shared in a messenger or a social network shows a preview. Without
an image the preview is a bare title, and a generic site picture says nothing about the
question. The site has several hundred questions in two languages, and the content
changes with every import.

Crawlers fetch an image once and keep it for a long time, so a URL must never serve two
different pictures, and an image must be cheap to serve. Rendering is CPU work that anyone
can trigger by requesting a valid URL.

## Decision

- **Library.** SkiaSharp (MIT) with `SkiaSharp.NativeAssets.Linux.NoDependencies`, so the
  slim runtime image needs no fontconfig or other system packages. ImageSharp is excluded:
  its licence is not free for commercial use. System.Drawing is not supported on Linux.
- **Rendering.** One 1200 by 630 PNG per live question and culture, plus one site card per
  culture. The renderer is a pure function of an `OgCard`, with hinting off and the
  fonts embedded (Inter SemiBold, JetBrains Mono Bold, both OFL), so the same card always
  gives the same bytes. The colours are the dark theme tokens; a test compares them with
  `tokens.css`.
- **Lazy, on demand.** Nothing is rendered at import. The first request for a card renders
  it, at most two at a time, and stores it in `App:OpenGraph:CacheDirectory`. Eagerly
  rendering every question after each import would spend CPU on cards no crawler asks for.
- **Fingerprinted URLs.** The URL is `/og/{lang}/{stackKey}/{slug}/{fingerprint}.png`, where
  the fingerprint is the first 16 hex characters of SHA-256 over a renderer version and
  every field of the card. The image is `immutable` for a year. A changed title or a
  redesign changes the fingerprint and so the URL; the old URL answers 301 to the new one
  for an hour, which keeps links that were shared earlier working. The endpoint always
  computes the current fingerprint from the database, so it never serves a picture that
  disagrees with the page.
- **Cache on a volume.** The files survive a container restart. A missing or unwritable
  directory only turns caching off, with one logged warning, because the image is worth
  more than the cache. After every committed import the whole directory is cleared: the
  fingerprint already makes stale files unreachable, so clearing only reclaims disk space
  and is cheaper than working out which questions changed.
- **Misses are empty 404s.** A retired question, an unknown slug, a missing locale or an
  unsupported culture answers an empty 404, with the status-code pages switched off. The
  route values are called `lang` and `stackKey`, not `culture` and `stack`, because the
  culture middleware judges those two names and would turn the 404 into the HTML page.
- **Pages without a card.** Every public page advertises the site card of its culture,
  and a retired question does too, since it has no card of its own.

## Consequences

- The runtime depends on a native library; the Linux package is the no-dependency build
  and adds a few megabytes to the image. The Windows and macOS assets come transitively,
  so development and tests run unchanged.
- The fonts add about 600 KB of embedded resources to the web assembly.
- The first request for a card pays the render; later requests read
  a file. Because anyone can request any valid URL, the render slots are limited to two,
  and a reverse proxy may cache the immutable responses on top.
- A redesign needs one bump of `OgFingerprint.Version`; no other invalidation is needed.
- Clearing the directory after an import means the first crawler visit to each question
  renders again, which is acceptable for a site this size.
