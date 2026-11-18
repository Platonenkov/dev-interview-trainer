# ADR 0027: The static edition decodes Brotli boot resources in the page

## Status

Accepted as the outcome of the Task 1 spike (`docs/static-edition-spike.md`, `COMPRESSION=brotli-decode`), to which the
design (section 5.9, "Compression") delegates the decision.

## Context

GitHub Pages serves a `.wasm` file gzip at best and never the precompressed `.br` files of the publish output. The spike
measured the cold file set of an island page: 2 784 251 bytes with `gzip -6`, above both the 2 300 000 byte rule and the
2.5 MB budget on its own. The `.br` files of the same set sum to 2 350 695 bytes. The runtime must therefore fetch the
`.br` files and decode them in the browser.

## Decision

1. **`App:StaticSite:BrotliRuntime`** (default `false`) switches the edition. With it on, `StaticAssetList` exports the
   `_framework/` `.br` routes next to their base routes and lists them in `Immutable` (a variant is immutable exactly when
   its base route is, so it matches `StaticAssetRules`), and `App.razor` marks the `islands-start.js` script tag
   `data-brotli="true"`. The files and the loader that asks for them read one setting. `.gz` variants are never exported.
2. **`islands-start.js` passes `loadBootResource` to `Blazor.start`** only on a static page that carries
   `data-brotli="true"`. For the types `dotnetwasm`, `assembly`, `pdb` and `globalization` it returns one promise chain:
   fetch `<defaultUri>.br`, import `js/vendor/brotli-decode.mjs` (a same-origin module, resolved against the script, so
   the meta CSP is unchanged), decode, verify the decoded bytes against the boot resource's `sha256-` integrity with
   `crypto.subtle.digest`, and answer `new Response(bytes, { headers: { 'content-type': ... } })` with `application/wasm`
   for `dotnetwasm` and `application/octet-stream` otherwise. The expected hash comes from the integrity table of
   decision 7, not from the `integrity` argument. Every failure (missing or corrupt `.br`, a decoder or table that does not
   load, no `crypto.subtle`, no `sha256-` value, a digest mismatch) lands in a `catch` that returns
   `fetch(defaultUri, { integrity })` with the table's value. A promise that resolves to `undefined` is not a request for default loading (only a
   synchronous `undefined` or `null` is, `dotnet.d.ts`), so every other type returns `undefined` synchronously and the
   handled path never resolves to it.
3. **Default cache mode for both fetches.** The brief wrote `cache: 'no-cache'`. Both URLs are content-addressed (the
   name changes when the bytes change), so a cached copy is never stale, and `no-cache` would force a revalidation round
   trip per file on every warm load when no service worker controls the page, against the warm target. Integrity on the
   fallback keeps a damaged cached copy from reaching the runtime.
4. **The decoder is Google's minified `decode.min.js`, vendored with the licence header prepended.**

   | Field | Value |
   |---|---|
   | Source | `github.com/google/brotli`, tag `v1.2.1`, path `js/decode.min.js` (`raw.githubusercontent.com/google/brotli/v1.2.1/js/decode.min.js`) |
   | Licence | MIT. The upstream file has no header, so the five-line header of `js/decode.js` of the same tag (`Copyright 2017 Google Inc.`, MIT) is prepended |
   | Upstream SHA-256 | `8a5348dbfb1a2873dbcb9a4d53f4a2d99638c113888b65b03cbb8ed86c7f7c27` (150 933 bytes) |
   | Vendored file | `src/Trainer.Web/wwwroot/js/vendor/brotli-decode.mjs` |
   | Vendored SHA-256 | `48a81bf38ffda7bf4fe2389132ff9ac780879afe169619b44945da051a43b3a9` (151 100 bytes; 66 881 bytes with `gzip -9`) |
   | Changes | Only the prepended header. The file already ends in `export let BrotliDecode=...`, so it imports as an ES module; the name changes to `.mjs` so that hosts serve it as a module. The readable `js/decode.js` of the tag has SHA-256 `7fd3ecb658c9c7e389eaf0aaf7e0adcc8ba47e043c21bdefb8c3caa83d893d35` (213 246 bytes, 73 303 gzipped) and was the first candidate; it costs 6 422 bytes more. |

   `StaticAssetsTests` pins the vendored file to the hash above. The vendor directory is excluded from the root-relative
   URL scan of scripts because it is third-party code that is never edited here; it contains no URL.
5. **Budget.** `.br` sum 2 350 695 + decoder 66 881 = 2 417 576 bytes against the 2 500 000 byte budget (3.3% margin, the
   spike's figure). The margin still rests on the unpinned SDK of the spike, so Task 18 re-measures.
6. **The decoder loads from its fingerprinted URL.** `import('./vendor/brotli-decode.mjs')` resolves to the plain route;
   the page's import map (`ImportMapDefinition.FromResourceCollection`, which Blazor uses for its own modules) maps that
   route to `js/vendor/brotli-decode.<fingerprint>.mjs`, which `StaticAssetRules` covers. `StaticEditionPageTests` pins
   that the map of a static island page names such a route.

7. **The integrity table.** The runtime passes an empty `integrity` to `loadBootResource` for every fingerprinted boot
   resource: the published boot configuration (embedded in `dotnet.js`) lists a sha256 per resource, but `dotnet.js` of
   .NET 10 reads the field `hash` of each asset and the configuration writes `integrity`, so the value never reaches the
   loader (checked in `dotnet.js` of the pinned SDK 10.0.203). No project setting changes that. The `.NET` build already
   computed the sha256 of every published runtime file (the `integrity` property of the static web asset endpoints), so
   with the Brotli runtime on the app serves `js/boot-integrity.{fingerprint}.mjs` (`BootIntegrityModule`,
   `BootIntegrityEndpoint`): `export default {"<path under _framework/>": "sha256-..."}`, sorted, named by a hash of its
   own text, listed in the export as an immutable file. The page imports it as `./boot-integrity.mjs` through the import map
   (`ToImportMap`), whose `integrity` section lists the table's own hash, so the browser verifies the table before the
   loader trusts it. The key is the path under `_framework/` of the file the loader fetches (`ru/X.resources.wasm` for a
   satellite assembly), because the name the runtime passes drops the culture folder. A table that is missing, or lacks a
   resource, only costs the saving: decoded bytes are never used without a verified entry, so that file loads uncompressed,
   as before, with the integrity the runtime passed (empty for a fingerprinted resource, the runtime's own default trust). `ExportVerifier` checks the table against the
   published bytes: the format, every entry's hash against its file, that every `.br` of a runtime file has an entry, that
   it decodes to its base file, and that the table's name is the hash of its text.

## Consequences

- The cold load transfers the `.br` files plus about 67 KB of decoder instead of the gzip set. Decoding costs CPU on the
  main thread per file; the live checks record the effect.
- The `.br` URLs are fingerprinted, so the service worker (ADR 0026) caches the ones the page requests; the fallback
  files are cached the same way. There is no precache of the variant the page does not use.
- The loader's `integrity` argument is only a fallback for a resource the table does not list (a future runtime that passes
  one).
- The decoder is imported through the import map's fingerprinted route, so the service worker (ADR 0026) caches it like
  any other fingerprinted file.
- The server edition is unchanged: the option defaults to off and the page never passes the loader.
- Upgrading the decoder is a deliberate act: replace the file from a new upstream tag and update the hash here and in
  `StaticAssetsTests`.

## Amendment: loader failure handling (final review)

- A failed import of the integrity table is no longer kept for the page's lifetime. It is retried once after 200 ms, and when
  both fail the next resource tries again, three times in all; only then is the empty table kept. A browser keeps a failed
  module fetch in its module map, so the retry does not import: it reads the fingerprinted URL and the integrity that the
  page's import map lists for it, fetches the file with that integrity (`cache: 'no-store'`, no query, so the bytes are the
  ones the map hashes) and parses the text in the exporter's exact format as data. Nothing is executed. The table is the
  root of trust for the decoded files, so it is never used unverified: no map entry, no integrity for the URL, a failed
  fetch (an integrity mismatch included) or any other text keeps the empty table. The console warning is printed once per
  page, not once per attempt. A transient failure therefore no longer turns every
  runtime file of the page into an uncompressed download.
- When the plain file also fails its integrity check, the loader still asks once more for the file's status only, so that a
  missing file reaches the runtime as the HTTP error it is. That request is a `HEAD`: the service worker stores any good `GET`
  under the file's content-addressed name, and bytes that just failed their check must not be kept there. A host that refuses
  the method (405, 501) leaves the original failure standing.
