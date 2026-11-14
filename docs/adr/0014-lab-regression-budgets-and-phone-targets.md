# ADR 0014: Lab timings are regression budgets; the spec's phone targets are a deployment acceptance criterion

## Status

Accepted by the owner on 2026-11-13. Changes spec 8.2 (where its targets are measured)
and the Plan 3 scope (a new acceptance criterion); the targets themselves are unchanged.

## Context

Spec 8.2 sets the island targets: on a mid-range phone over 4G, Practice is
interactive within 2 s with a warm cache and within 5 s cold. Task 21 built a lab
measurement: the published build in headless Chromium under a fixed throttling
profile (150 ms latency, 9 / 1.5 Mbit/s, CPU 4x, 375x812, touch). After the fixes and
Stage 2 (invariant globalization, ADR 0013) the lab medians are about 7.7 s cold and
4.3 s warm in both languages, with the transfer at about 1.94 MB.

The start-up profile showed the remaining time is CPU work of the .NET WebAssembly
runtime on the throttled CPU. The "4x" factor is relative to whatever machine runs it
(it measured 5x on a JavaScript loop on the developer machine), so the lab emulation is
not a faithful stand-in for a real phone, and holding it to the real-phone numbers
would keep the build red without saying anything about users.

## Decision

- The lab emulation keeps its throttling profile and becomes a regression guard with
  its own budgets: Practice interactive median under **8 500 ms cold** and **4 750 ms
  warm** (both languages, 5 runs each), and the `_framework/` transfer of a cold load at
  most **2.5 MB**. The transfer budget fails every run; the timing budgets fail only
  when `DIT_PERF_ENFORCE=1` (the CI `release` job dispatched with `enforce = true`), and
  pull request and push runs record them.
- Spec 8.2's targets stay in the spec unchanged and become an acceptance criterion of
  Plan 3 (deployment): measured on the deployed site with a real mid-range phone over
  4G, opening the island page with `?perf=1` (the readout shows the milliseconds from
  navigation start to `data-island-ready` and the `_framework/` bytes), 5 cold and 5 warm
  runs in Russian and English. The localhost/USB procedure in `docs/performance.md`
  remains the detailed way to measure a phone before deployment.

## Consequences

- An enforced CI run can pass while the real-phone targets are not yet shown; Plan 3
  closes that gap and cannot be accepted without the phone measurement.
- The regression budgets sit about 10% above the measured lab medians; a change that
  slows start-up by more than that, or grows the runtime past 2.5 MB, fails the
  enforced run. Raising a budget needs the owner, as before.
- If the phone measurement misses the targets, the options recorded in
  `docs/performance.md` (AOT, a server-rendered first view, fewer start-up methods)
  come back as owner decisions in Plan 3.
