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
  and `analysis: { windowHours, generatedAt }`.
- `Finding.severity` is the string enum; `evidence[]` mandatory and non-empty;
  `confidence` optional string; `Incident` per the existing section, with
  `devicesLost[{vidPid,name}]`, optional `rootEvent`, `sharedParent`, `power`.
- `GET /contract` includes them when the recorder has history; producers keep
  it cheap (analysis is over the on-disk JSONL, no re-probe).
Producers:
- macOS: `Diagnosis` findings + `Collector.deriveIncidents` mapped to schema;
  `Severity` gets string raw values.
- Windows: `Finding` gains `Evidence`; `SnapshotComparer`/`PowerDiagnosis`
  findings and `IncidentStitcher` incidents mapped to schema. Ranked power
  deficit engine (persistence, gap tolerance) ported from `Diagnosis.swift` so
  the Surface gets "Power supply under-served" too.
Dashboard:
- Parse optional `findings`/`incidents` (tolerant: absent ≠ empty).
- **Per-host freshness and completeness (issue #29).** `/contract` and
  `/events` tracked independently per host with last successful contact;
  `skippedLines`, parse failures and `/events` fetch failures surfaced on that
  host (today `sources.ts` swallows them, contradicting the archived
  `contract-ingest` spec); each host shown as live / stale / offline /
  envelope-only / history-incomplete with documented thresholds; retained
  stale data visibly stale.
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
