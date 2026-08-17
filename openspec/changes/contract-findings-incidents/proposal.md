# Proposal: contract-findings-incidents

## Why
TBDoctor's reason to exist is a ranked list of findings, each with the evidence
that produced it. Today that list reaches only the native Timeline window,
`--report` and MCP — not the dashboard, which is supposed to be the UI for
both platforms. `schema-v1.md` defines `Finding` and `Incident` shapes but never
says where they live; neither producer emits them; the dashboard has a
`parseFinding` with no caller and recomputes incidents itself. Retiring the
macOS native windows (`macos-resident-serves-dashboard`) before this lands
would delete the headline feature.

Two latent bugs sit in the way and will bite the moment either producer tries:
the C# `Finding` has no `Evidence` field (the dashboard parser rejects findings
without evidence, by design), and Swift's `Severity` is an `Int` enum that
would encode as `0/1/2` instead of `info/warning/critical`.

## What
Contract (additive, stays v1):
- Envelope gains optional `findings: Finding[]` and `incidents: Incident[]`
  and `analysis: { windowHours, generatedAt, coverage: {availableFrom,
  through, complete, reasons?} }` — the recorder states what it can vouch
  for, so consumers can tell an empty stream from a new recorder from a
  trimmed log.
- `Finding.severity` is the string enum; `evidence[]` mandatory and non-empty;
  `confidence` optional string; `Incident` per the existing section, with
  `devicesLost[{vidPid,name}]`, optional `rootEvent`, `sharedParent`, `power`.
- `GET /contract` includes them when the recorder has history; producers keep
  it cheap (analysis is over the on-disk JSONL, no re-probe).
- **Baseline state and control (issue #36).** `analysis.baseline:
  {state: "no-baseline" | "healthy" | "active-fault" | "recovered", capturedAt?,
  faultSince?, recoveredAt?}` so the dashboard never reads "no baseline" as
  healthy; the Windows baseline-diff findings ("display active but hub branch
  missing") flow into `findings[]` like any other. The dashboard gains
  **Capture baseline** / **Replace baseline** (explicit confirmation naming
  the old capture time) backed by `POST /baseline` — the first state-changing
  route in `docs/embedding.md` § Mutations: loopback-only **and**
  same-origin (`Origin` must equal the served origin; custom
  `X-ConnectionDoctor-Request` header forces a preflight that mutation routes
  answer without CORS headers; no wildcard `Access-Control-Allow-Origin` on
  mutations), because CORS gates reads, not writes — any page in the browser
  could otherwise POST to localhost. Replace is conditional on `If-Match:
  <capturedAt>` (409 when stale) so a second tab cannot clobber a newer
  baseline; 403 distinguishes origin/binding rejection.
Producers:
- macOS: `Diagnosis` findings + `Collector.deriveIncidents` mapped to schema;
  `Severity` gets string raw values.
- Windows: `Finding` gains `Evidence`; `SnapshotComparer`/`PowerDiagnosis`
  findings and `IncidentStitcher` incidents mapped to schema. Ranked power
  deficit engine (persistence, gap tolerance) ported from `Diagnosis.swift` so
  the Surface gets "Power supply under-served" too.
Dashboard:
- Parse optional `findings`/`incidents` (tolerant: absent ≠ empty).
- **Per-host contact and history quality, as two axes (issue #29, refined
  in review of #34).** `/contract` and `/events` tracked independently per
  host with last-success timestamps; **contact** = `live | stale | offline`;
  **history** = `complete | no-history | envelope-only | incomplete` with
  durable reasons (`skippedLines`, truncation, fetch failure, recorder
  coverage shorter than the window). Completeness comes from the producer's
  `analysis.coverage`, not from guessing at the first event; an incomplete
  reason clears only when a later payload proves the requested window
  complete. Today `sources.ts` swallows `/events` failures and drops
  `skippedLines`, contradicting the archived `contract-ingest` spec.
- New **Findings** panel next to the timeline: ranked by severity then
  confidence, each with evidence list and recommendation; "recorded" label;
  when a collector sends none, say "this collector reports no findings" (and
  distinguish "no recording yet" via `analysis` absent). "No findings" / "no
  incidents" is claimed **only when the analysis window is complete**;
  otherwise the panel says unknown / incomplete and why.
- Timeline uses producer incidents when present, its own stitching otherwise,
  and says which.

## Non-goals
Making the two engines agree (that is `contract-conformance` with fixtures);
new finding types; a v2.

## Impact
`docs/schema-v1.md`; `Contract.swift`, `Diagnosis.swift`; `ContractV1.cs`,
`Models.cs`, new `DeficitAnalysis.cs`; dashboard `contract/types.ts`,
`parse.ts`, new `components/FindingsView.tsx`, `TimelineView.tsx`.
Capabilities: `contract-v1` (new, producer-side), `contract-ingest`,
`findings-view` (new).
