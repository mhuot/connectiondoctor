# Proposal: contract-conformance

## Why
The analysis layer is written twice (Swift, C#) plus a TypeScript copy in the
dashboard, and a review on 2026-08-16 found 25 places where they or the spec
disagree — 13 pieces of logic implemented two or three times. Nothing breaks
interop today, but the same desk produces a different picture depending on
which OS reports it: Windows never emits `power.adapter` (the dashboard then
says "No adapter — nothing is supplying power"); Windows emits monitors as
`kind:"display"` nodes while macOS uses `displays[]` and the dashboard reads
only the latter; `tunneled` means "any USB3 when a TB device exists" on macOS
and always `false` on Windows; Windows never writes `linkDown`/`linkUp` so its
incidents can never have a root event; the macOS contract tree parents every
USB root under the first TB device, disagreeing with TBDoctor's own topology.

## What
1. **Fixtures.** `docs/fixtures/`: real recordings (envelopes + events JSONL)
   from the fleet — M3 Pro on the Surface dock chain, Mac mini KVM flip,
   Surface Laptop 7 — plus the *expected* `findings[]`/`incidents[]` for each.
   **Negative controls too (issue #31):** normal unplug/replug, sleep/wake, a
   KVM migration, duplicate VID:PID devices, an incomplete-history window —
   cases whose expected answer is "no finding" or "unattributed". Tests are
   split into **parity** (all engines agree) and **diagnostic quality** (the
   expected cause and confidence are right, and nothing fires on a control).
   **Identity fixtures (issue #27):** a hostname reused across two machines, a
   renamed host, two identical docks on different endpoints.
2. **Analysis reads Contract v1.** Swift `Diagnosis`/`Collector` incident
   derivation and C# `SnapshotComparer`/`PowerDiagnosis`/`IncidentStitcher`
   take envelopes/events, not `Sample`/`ConnectionSnapshot`. Probe → envelope
   remains the only native step.
3. **Conformance test on all three.** Each side has one test that loads every
   fixture and asserts findings/incidents equal the expected JSON (order,
   severities, evidence count, sharedParent). Same for the dashboard's TS
   domain (`incidents.ts`, `topology.ts` collapse).
4. **Spec decisions written down**, then implemented on both:
   - `tunneled` = has a `thunderbolt` ancestor and protocol is `usb3`|`displayPort`|`pcie`.
   - Displays: `displays[]` is canonical; `kind:"display"` nodes are allowed
     only with `attachedTo` back-reference; dashboard reads both.
   - `power.adapter` always present when `externalConnected`; `identifiesItself`
     false when unknown. `power.source` rule: `dock` iff adapter does not
     identify itself *or* the supplying port has a `thunderbolt` node.
   - `hub` ⇔ `usbClass == 9` or has children; never by name.
   - `linkDown`/`linkUp`: poll-derived allowed on both; optional `source: kernel|poll`.
   - Deficit: events −2 W instantaneous; finding ≥10 W sustained, 2 samples,
     gap tolerance 3 — one paragraph in the spec, both engines cite it.
   - `adapterChanged` = identity/rating change; new `acChanged` for AC toggle.
   - JSONL trim forces a fresh `fullSnapshot` on both.
   - `host.arch` enum `arm64|x86_64`; timestamps with offset on both;
     `tb.route` integer.
   - **Stable identity (issue #27):** `host.id` = hashed platform UUID
     (IOPlatformUUID / MachineGuid), emitted by both; the dashboard keys hosts
     on it and falls back to `host.name`. `node.serialHash` (opt) when the OS
     exposes a serial — hashed, never raw — so two same-model docks are
     distinguishable. Both additive.
5. **Layout copies converge.** Excalidraw export moves to
   `dashboard/src/domain/excalidraw.ts` over the TS layout engine (already
   invariant-tested). `scripts/build-ui` also emits it as one self-contained
   `excalidraw.js`; the collectors embed it and run it with JavaScriptCore
   (macOS, in-box) / Jint (Windows, pure .NET) for the `excalidraw` verb and
   `connection_diagram`. `Diagram.swift`/`ExcalidrawExport.swift` are deleted.

## Non-goals
A shared runtime (Rust core) — deferred until conformance shows the cost is
real; a v2 of the contract.

## Impact
`docs/schema-v1.md` (rules), `docs/fixtures/` (new), macOS `Diagnosis.swift`,
`Contract.swift`, `ContractLog.swift`; Windows `ContractV1.cs`, `Recorder.cs`,
`SnapshotComparer.cs`, `BackgroundCollector.cs`; dashboard `domain/*`, new
`domain/excalidraw.ts`; `scripts/build-ui` (second output).
Capabilities: `analysis-conformance` (new), `contract-v1`.

## Depends on
`contract-findings-incidents` (the shapes being asserted). Informed by
`add-windows-mcp`, `align-cli-verbs`, `macos-resident-serves-dashboard`.
