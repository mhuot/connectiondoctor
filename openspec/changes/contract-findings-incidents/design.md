# Design: contract-findings-incidents

## Where findings live: in the envelope, optionally
Alternatives: a new `/findings` route (more embedding surface, another thing to
keep identical), or a separate `report` document. Optional envelope fields keep
one payload, one parser, and let `probe --json`, `connection_probe` and
`GET /contract` all carry the analysis. `analysis: {windowHours, generatedAt}`
lets the dashboard label the panel honestly ("last 6 h, generated 12:04").

Cost concern: `/contract` today re-probes hardware; adding analysis over a
24 MB JSONL on every fetch could be slow. Both producers already tail the log
incrementally (`Collector`, `DashboardDataLoader.EventLogCursor`); reuse that
so analysis is over in-memory state, not a re-read.

## Shape fixes
- Swift `Severity: String` raw values `info|warning|critical`; JSON encoding
  matches the schema. Native code keeps ordering via a `rank` computed property.
- C# `Finding` gains `IReadOnlyList<string> Evidence` (required); every
  existing finding site supplies at least one line (they all have the fact that
  triggered them — the missing branch, the discharge rate).
- Incident: producers emit `devicesLost` as `{vidPid, name}` objects (Swift has
  names only today; it has the vidPid in the sample). `power.peakDischargeMilliwatts`
  in mW (Swift's mA × V).

## Windows ranked engine
Port `Diagnosis.deficitPeriods` (≥10 W sustained, min 2 samples, gap
tolerance 3, whole-run qualification) so the finding fires on the same
evidence on both OSes. The event-log deficit rule (−2 W instantaneous) stays as
is — spec already distinguishes them once `contract-conformance` writes it
down.

## Host contact and history (issue #29; split after review of #34)
Two independent statuses, because they overlap in every combination (offline
*and* incomplete; live *and* envelope-only; stale *and* complete):

- **contact** — `live` (contract or events succeeded within 2× the refresh
  interval), `stale` (older than that; data retained and shown as stale),
  `offline` (last refresh failed for both). `HostData.contact = {contractAt?,
  eventsAt?, contractError?, eventsError?}` keeps the timestamps separately.
- **history** — `complete`, `no-history` (recorder never ran: `analysis`
  absent), `envelope-only` (`/events` failed or empty while `/contract`
  succeeded), `incomplete` with **durable reasons**: `skippedLines > 0`,
  `coverage.complete == false` (with the producer's `reasons`), events window
  shorter than `analysis.windowHours`. A reason is cleared **only when a later
  payload proves the window complete** (`coverage.complete` true for the
  requested window and zero skipped lines) — a successful refresh does not
  restore lines that were skipped or trimmed earlier.

Completeness is never inferred from the first event alone; it comes from
`analysis.coverage`, which the producers compute from the recorder's actual
span, trims and gaps. Both statuses show as chips on fleet cards and the host
selector; the Findings and Timeline panels read `history` to decide whether
"none" is a claim they can make. Thresholds are constants in one place and
named in the spec.

## Dashboard panel
`FindingsView`: list, not cards; severity chip; title; explanation; evidence as
a bulleted list in monospace; recommendation; confidence muted. Sorted
critical → warning → info, then confidence (very high → high → moderate →
absent). Empty states: `analysis` absent → "no recording yet on this
collector — run `install`"; `findings: []` → "no findings in the last N h".
Timeline: when `incidents` present, use them and label "incidents from
collector"; else stitch and label "incidents derived by dashboard".
