# Static edition spike

Record of Task 1 of Plan 5 (`docs/superpowers/specs/2026-11-17-static-edition-design.md`). It proves that a published
Release island boots from a plain static host under the `/dev-interview-trainer/` prefix and fixes the compression
decision that later tasks depend on. Measured on 2026-11-17. No production code changed.

## Decisions

| Id | Decision | Result |
|---|---|---|
| a | Boot works | Yes. `.island-root[data-island-ready='true']` appeared in 0.8 to 1.2 s on localhost, with no console error, no failed request and no CSP violation. |
| b | Requests of the island | 63 requests, all under `/dev-interview-trainer/`, none to `resource-collection`, none to an API or other runtime-generated endpoint. The full list is at the end. |
| c | `COMPRESSION=brotli-decode` | The `gzip -6` cold sum is 2 784 251 bytes, above the 2 300 000 byte limit of the rule. The host serves gzip, but not brotli. |
| d | Deterministic | Yes, with one comparison rule (see Determinism). |

## Method and environment

- Publish: `dotnet publish src/Trainer.Web -c Release -o artifacts/spike-X -p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static`.
- The SDK was 10.0.203 with the `wasm-tools` manifest 10.0.107 (runtime packs 10.0.7), not the 10.0.303 pair that
  the `release` job of `ci.yml` pins; no 10.0.3xx SDK was installed on the machine. The byte sizes below therefore
  come from a different relink than CI. Task 18 must repeat the determinism check and the size sums once on the
  pinned pair. That re-measure is a gate of Task 18, not a note: the brotli margin is 3.3% and is proven only for the
  10.0.203 / wasm-tools 10.0.107 pair on which every size in this record was measured.
- PostgreSQL: the image pinned in `deploy/compose/compose.yaml`, on port 55432; `dotnet ef database update`; the app
  ran in the Testing environment with `App:PublicBaseUrl=http://localhost:8099/` and `App:ContentRoot` pointing at `content/`.
- The page `/en/dotnet/practice` and every non-compressed route of the app's endpoints manifest (140 files) were
  saved with plain HTTP requests (identity encoding), then the app was stopped.
- By hand: files under `site/dev-interview-trainer/`; `<base href>` and every root-relative `src`, `href`, `action` set to the
  prefix; the import map nonce removed; a meta CSP as the first element of `<head>` with `default-src 'self'`,
  `script-src 'self' 'wasm-unsafe-eval'` plus the SHA-256 of the two inline scripts (the theme script and the import
  map), `style-src 'self'`, `img-src 'self' https: data:`, `font-src 'self'`, `base-uri 'self'`, `form-action 'self'`; a
  referrer meta. Served by a throw-away PowerShell static server (`tools/spike/serve-static.ps1`, removed once `PagesEmulator` existed) on port 8099.
- Browser: Playwright 1.64.1 Chromium, headless, a fresh context (cold cache). A request listener recorded every
  request; a `securitypolicyviolation` listener recorded CSP violations.

## Findings that change later tasks

1. CSS carries root-relative URLs. `css/site.css` has four `url("/fonts/...woff2")` references. Rewriting only the HTML
   sends the four font requests to `/fonts/...` (404, outside the prefix); the first browser run showed exactly that.
   The static export must rewrite CSS `url()` (the spec already lists it in the structural check), or the source CSS
   must use relative URLs (`../fonts/...`), which resolve against the stylesheet on any prefix and keep the content
   fingerprint truthful. A rewrite changes the bytes under an unchanged fingerprint.
2. Fonts are referenced by their plain names and have no `.br` variant; the fingerprinted font routes exist in the
   manifest but the page does not use them.
3. The island markup and the persisted component state carry no URL (the `Blazor:` marker holds the assembly, the type
   name and the bootstrap JSON as parameters). The two `_framework/resource-collection.*` entries stay in the import map
   as inert mappings; nothing requests them.

## Determinism

- `spike-a`, then `spike-b` after changing `content/dotnet/architecture/aggregate-consistency-boundary-transaction.yaml`
  in the working tree (restored afterwards; no throw-away commit, because the version is the constant `static` and the
  commit cannot matter): all 205 files under `wwwroot/` have identical SHA-256, and `Trainer.Web.dll` is identical. The
  content files are read from `content/` at run time and do not enter the publish output.
- `spike-c` is a clean rebuild after deleting every `bin` and `obj` directory under `src/`: all 205 `wwwroot/` files are
  identical to `spike-a` again. `spike-b` reused the incremental build, so `spike-c` is the stronger proof.
- Gate result: identical. No property is needed beyond `-p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion=static`.
- The brief's gate pair, `spike-a` vs `spike-b`: `Trainer.Web.staticwebassets.endpoints.json` is byte-identical
  (the same SHA-256, `2086d45a...`), because both publishes reused one incremental build and one set of file times.
- Not identical: `Trainer.Web.staticwebassets.endpoints.json` of `spike-c` differs from `spike-a` in 626 of 668 endpoints, and
  only in the `Last-Modified` response header (the modification time of the build output). Route, `AssetFile`, selectors,
  `Content-Length`, `ETag`, `integrity` and `original-resource` are identical. Task 18 must compare the endpoints manifest
  without `Last-Modified`, or hash only Route, AssetFile and the endpoint properties. The static site does not use
  that header: GitHub Pages sets its own.

### Fingerprint format (input for `StaticAssetRules`, Task 14)

- A fingerprint is exactly 10 characters of `[a-z0-9]`, inserted before the last extension: `name.<fp>.ext`
  (`css/site.we5d1p27ih.css`, `js/shortcuts.7uea53adhm.js`, `fonts/Inter-Regular.s55nqzfd5b.woff2`,
  `_framework/dotnet.runtime.2zl32tp6ah.js`, `_framework/Trainer.Domain.0h7am04uai.wasm`). All 199 fingerprinted routes
  of the manifest that have a plain sibling (156 under `_framework/`, 24 `js/`, 10 `fonts/`, 9 `css/`) match
  `^(.+)\.([a-z0-9]{10})\.([A-Za-z0-9.]+)$`; no route with a 10-character token lacks a plain sibling.
- Culture folders keep the folder: `_framework/en/Trainer.Web.Client.resources.ug5gr7ue0f.wasm`.
- Physical files differ by directory. Under `_framework/` the published files carry the fingerprint
  (`System.Private.CoreLib.uwdlkuw7g6.wasm`), and so do their `.br` and `.gz` variants; `blazor.web.js`,
  `blazor.webassembly.js`, `blazor.server.js` and `dotnet.js` also exist unfingerprinted. Under `css/`, `js/` and `fonts/`
  the physical files have the plain name, and the fingerprinted name exists only as a route (`AssetFile` is the plain
  file or its `.br`). The static site needs the fingerprinted copies that pages reference and the plain names that the
  import map uses as keys.
- The runtime-generated route in the page is `_framework/resource-collection.X029I.js` and its `.gz`. Its token is
  5 characters of mixed case and is not a fingerprint of the format above.
- Response headers by route in the manifest: fingerprinted `max-age=31536000, immutable`; plain `no-cache`.

## Compression of the cold load

The cold file set is the 63 requests listed below: the page, three stylesheets, four fonts, nine scripts and 46 `fetch`
resources (the runtime `.wasm` and 45 assemblies).

| Measure | Bytes |
|---|---|
| Raw sum | 6 601 002 |
| `gzip -6` sum | 2 784 251 |
| `gzip -9` sum | 2 743 954 |
| Publish `.gz` sum (fonts raw) | 2 834 385 |
| Publish `.br` sum (fonts raw, 412 832; brotli quality 11 for the page and the stylesheets) | 2 350 695 |
| Google `decode.min.js` (brotli v1.1.0, `js/decode.min.js`, MIT, an ES module): raw / `gzip -9` | 150 869 / 66 637 |
| Brotli path total (`.br` sum plus the decoder, `gzip -9`) | 2 417 332 |

Largest items (raw, `gzip -6`, `.br`): `System.Private.CoreLib` 1 618 709, 624 375, 502 361; `dotnet.native.wasm`
1 496 849, 583 078, 468 305; `System.Text.Json` 385 301, 154 554, 128 583; `Trainer.Web.Client` 357 141, 145 554, 115 910.
The woff2 fonts do not compress. The gzip sums come from one implementation (Python `zlib`, no timestamp in the header).
The decoder was read from `raw.githubusercontent.com/google/brotli/v1.1.0/js/` and measured in a pipe; nothing was saved.

### Host evidence

`https://kripken.github.io/sql.js/dist/sql-wasm.wasm`, a plain GitHub Pages site, with `Accept-Encoding: gzip, br`:
`Server: GitHub.com`, `Via: 1.1 varnish`, `X-Served-By: cache-gig2250043-GIG` (a Fastly-style cache node name), `X-Cache: MISS`
on the first request and `HIT` on later ones, `Content-Type: application/wasm`, `Content-Encoding: gzip`,
`Content-Length: 508708` for a 1 450 157 byte file, `Cache-Control: max-age=600`, a weak `ETag`. The headers show a
caching layer in front of the origin; they do not name its operator, so this record does not claim that no CDN is involved.
The `Server: GitHub.com` header is the criterion of the brief and it holds.
With `Accept-Encoding: br` alone the response has no `Content-Encoding` (1 450 157 bytes): the host does not serve
brotli. A second public sample, `https://mozilla.github.io/pdf.js/build/pdf.worker.mjs` (not a `.wasm`; no second
`.wasm` of 100 KB or more was found quickly), shows the same headers: `Server: GitHub.com`, `Via: 1.1 varnish`,
`Content-Encoding: gzip`, `Content-Length: 508884`, `Cache-Control: max-age=600`; pages on `emscripten.org` and
`rustwasm.github.io` are gzip with `max-age=600` too. The host's gzip is 0.3% larger than local `gzip -6` of the same file (507 010) and 1.2% larger than `gzip -9`
(502 550).

### Decision

Rule: `host-gzip` when the host shows gzip and the `gzip -6` cold sum is at most 2 300 000 bytes, otherwise
`brotli-decode`. The host serves gzip, but the sum is 2 784 251: 484 251 bytes (21%) over, and over the 2.5 MB budget
by itself.

**COMPRESSION=brotli-decode.** The `.br` cold sum 2 350 695 fits the 2 500 000 byte budget with 6% margin; with the
decoder, 2 417 332 (3.3% margin). The margin is thin and rests on an unpinned SDK (see Method), so Task 18 re-measures.

Warm path: the host sends `Cache-Control: max-age=600`, so a warm visit revalidates after ten minutes and the
`immutable` lifetime of the fingerprinted routes is not available; ETag revalidation costs a round trip per file.
Task 14 and the service worker decide what to do with it.

## Requests of the island page (cold, 63)

```
/dev-interview-trainer/en/dotnet/practice/
/dev-interview-trainer/css/tokens.wsm8sf6yvl.css
/dev-interview-trainer/css/site.we5d1p27ih.css
/dev-interview-trainer/css/islands.7uf86sshtx.css
/dev-interview-trainer/_framework/dotnet.pos3oamju9.js
/dev-interview-trainer/js/theme-toggle.30uddet0wx.js
/dev-interview-trainer/js/outbox.bgxwjbmlg6.js
/dev-interview-trainer/fonts/JetBrainsMono-Bold.woff2
/dev-interview-trainer/fonts/Inter-Regular.woff2
/dev-interview-trainer/fonts/JetBrainsMono-Regular.woff2
/dev-interview-trainer/fonts/Inter-SemiBold.woff2
/dev-interview-trainer/js/shortcuts.7uea53adhm.js
/dev-interview-trainer/js/page-lifecycle.bvnmy97cp3.js
/dev-interview-trainer/_framework/blazor.web.hwaxidwiwj.js
/dev-interview-trainer/js/islands-start.2lxk0hyl79.js
/dev-interview-trainer/_framework/dotnet.runtime.2zl32tp6ah.js
/dev-interview-trainer/_framework/dotnet.native.15vn8m5hb3.js
/dev-interview-trainer/_framework/dotnet.native.ryyt2t16i5.wasm
/dev-interview-trainer/_framework/System.Private.CoreLib.uwdlkuw7g6.wasm
/dev-interview-trainer/_framework/System.Runtime.InteropServices.JavaScript.6w9sqvnmwo.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Authorization.288bcwkpa4.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Components.6kmdzvwwox.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Components.Authorization.7jj139lqhj.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Components.Web.22vybn36z9.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Components.WebAssembly.Authentication.ar62dlqcvl.wasm
/dev-interview-trainer/_framework/Microsoft.AspNetCore.Components.WebAssembly.dfwbiv4nbv.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Configuration.Abstractions.3lgtxxm3jb.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Configuration.Json.2673pa34ah.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Configuration.m57ujhbjop.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.DependencyInjection.Abstractions.6fluizfuil.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.DependencyInjection.5ivmu7a55y.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Diagnostics.Abstractions.e1zcqjq3ka.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Diagnostics.p2a64fgguz.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Localization.Abstractions.e8vaw98nbq.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Localization.xotl1bkoda.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Logging.Abstractions.bccurhy1pq.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Logging.g2a3t0y5kl.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Options.acibwsk3ey.wasm
/dev-interview-trainer/_framework/Microsoft.Extensions.Primitives.r0c30bslaw.wasm
/dev-interview-trainer/_framework/Microsoft.JSInterop.WebAssembly.igy9hupfat.wasm
/dev-interview-trainer/_framework/Microsoft.JSInterop.wnhlzzgi71.wasm
/dev-interview-trainer/_framework/System.Collections.wt4x7kamz8.wasm
/dev-interview-trainer/_framework/System.Collections.Concurrent.lhnl6vkgq1.wasm
/dev-interview-trainer/_framework/System.Collections.Immutable.hpl3en4nik.wasm
/dev-interview-trainer/_framework/System.ComponentModel.xmxjt0ntly.wasm
/dev-interview-trainer/_framework/System.Console.4t85rcb862.wasm
/dev-interview-trainer/_framework/System.Diagnostics.DiagnosticSource.lwx4cqjd1p.wasm
/dev-interview-trainer/_framework/System.IO.Pipelines.r4k8j9p1r8.wasm
/dev-interview-trainer/_framework/System.Linq.md997n04tw.wasm
/dev-interview-trainer/_framework/System.Memory.60lsd7b7gy.wasm
/dev-interview-trainer/_framework/System.Net.Http.8d9m004uvq.wasm
/dev-interview-trainer/_framework/System.Net.Http.Json.ey5t6glqm0.wasm
/dev-interview-trainer/_framework/System.Net.Primitives.xmwuhmteav.wasm
/dev-interview-trainer/_framework/System.Private.Uri.rj7g34mx0q.wasm
/dev-interview-trainer/_framework/System.Security.Claims.0aska09fsb.wasm
/dev-interview-trainer/_framework/System.Security.Cryptography.le5r1xop5j.wasm
/dev-interview-trainer/_framework/System.Text.Encodings.Web.6q9wopfhty.wasm
/dev-interview-trainer/_framework/System.Text.Json.ewxihbck8y.wasm
/dev-interview-trainer/_framework/System.Text.RegularExpressions.y7k11a2fwz.wasm
/dev-interview-trainer/_framework/Trainer.Contracts.f38qp9pqp8.wasm
/dev-interview-trainer/_framework/Trainer.Domain.0h7am04uai.wasm
/dev-interview-trainer/_framework/Trainer.Web.Client.kljathmvmb.wasm
/dev-interview-trainer/_framework/en/Trainer.Web.Client.resources.ug5gr7ue0f.wasm
```
