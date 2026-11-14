# ADR 0013: The WebAssembly client runs with invariant globalization

## Status

Accepted (owner decision 2026-11-13, "Stage 2" of the performance plan). Spec 8.3 keeps
its guarantee (culture from `<html lang>`, fixed for the runtime's lifetime, a page never
mixes languages); its sentence naming `applicationCulture` as the mechanism now names
`PageCulture` and points here.

## Context

A cold island load downloaded about 2.6 MB of runtime, over the 2.5 MB budget
(`docs/performance.md`). The ICU data shard alone was 0.14 MB (`en`) or 0.22 MB (`ru`),
and with the `wasm-tools` workload the relinked runtime also carries the ICU code. The
islands use culture data in only a few places: three dates on the profile (formatted with
`"g"` and `"d"`), integers (which format the same in every culture without grouping), and
`TwoLetterISOLanguageName` for plural forms, the sign-in link and the source-language
marker.

## Decision

- `Trainer.Web.Client` sets `InvariantGlobalization=true` and `PredefinedCulturesOnly=false`:
  no ICU data is shipped, and a named culture can be created without data.
- Under invariant globalization the runtime ignores Blazor's `applicationCulture` and
  starts with the invariant culture, which would select the neutral resources. `PageCulture.Apply`
  (`Interop/PageCulture.cs`, first thing in `Program.cs`) reads `<html lang>` through a
  `[JSImport]` of `window.ditPageLanguage` and sets the default and current cultures to
  that named culture. The host then loads its satellite assembly when it starts, so `.resx`
  lookup works exactly as before (verified in the published build in both languages).
  Hybrid globalization is not available for browser WebAssembly in .NET 10.
- Nothing reads culture data: `UiLanguage.Of(culture)` takes the language from the culture
  name (`PluralForms`, `SignInLink`, `FeedbackPanel`).
- Dates are formatted by the browser. `LocalTime` renders `<time datetime="…">` with text from
  `ILocalTimeFormatter`, whose browser implementation is a synchronous `[JSImport]` of
  `window.ditFormatTime` (`islands-start.js`): `Intl.DateTimeFormat(language, {year, month,
  day: 'numeric'[, hour: 'numeric', minute: '2-digit']})` in the device's time zone, for
  example `08.03.2026, 18:55` (ru) and `3/8/2026, 6:55 PM` (en). The values come from the
  server as instants; formatting them on the server would need the device's time zone for
  every past date, which the browser already has.
- Durations (mock timer) were already culture-independent (`m:ss`).

## Consequences

- A cold load is about 1.94 MB locally (relinked) instead of about 2.6 MB, and start-up
  skips loading ICU data. In alternating paired runs a cold load became 0.7-1.0 s faster
  locally; a warm load gained at most about 0.2 s, which the repeats do not establish
  (`docs/performance.md`).
- The saving depends on the `wasm-tools` workload relinking the runtime: relinked,
  `dotnet.native.wasm` is 468 KB Brotli; the prebuilt runtime (no workload) still
  carries the ICU code at 977 KB, which puts a cold load at about 2.44 MB, only about
  60 KB under the 2.5 MB budget. CI pins the workload set (`dotnet workload install
  wasm-tools --version 10.0.303`, matching the SDK feature band) and the Plan 3 image
  must do the same.
- New client code must not use culture-sensitive formatting (`ToString("d")`, `"N0"`,
  culture comparisons) or `TwoLetterISOLanguageName`; dates go through `LocalTime`, the
  language through `UiLanguage`. `CultureFormattingTests` simulate a culture without data,
  and the Release suite checks the profile's dates and plural forms in both languages.
- Date strings now follow the browser's `Intl` output, which differs slightly from .NET's
  short patterns (a comma between date and time).
