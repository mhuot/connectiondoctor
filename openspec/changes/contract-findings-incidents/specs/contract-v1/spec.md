## ADDED Requirements

### Requirement: Envelope carries findings and incidents
Producers SHALL include optional `findings: Finding[]`, `incidents: Incident[]` and `analysis: {windowHours: number, generatedAt: string, coverage: {availableFrom, through, complete: boolean, reasons?: string[]}}` in the v1 envelope when recorded history exists, and SHALL omit all three when it does not. `coverage.complete` SHALL be true only when the recording spans the whole requested window with no trim inside it and no gap longer than 3× the sample interval; otherwise `reasons` SHALL say why. This is additive within v1.

#### Scenario: Baseline state is explicit
- **WHEN** no baseline has been captured
- **THEN** `analysis.baseline.state` is `no-baseline` and no baseline-diff finding is emitted — absence is not health; **WHEN** a missing branch returns after a fault, the state is `recovered` with `faultSince`/`recoveredAt`

#### Scenario: Trimmed log
- **WHEN** the JSONL was trimmed inside the requested window
- **THEN** `coverage.complete` is false with reason `trimmed` and `availableFrom` is the first retained sample, so a consumer shows "unknown before <time>", not "no incidents"

#### Scenario: Collector with history
- **WHEN** `GET /contract` (or `probe --json`, or `connection_probe`) is served on a machine whose recorder has run
- **THEN** the envelope contains `analysis` and the arrays (possibly empty), and the same content is returned by all three doors

#### Scenario: Fresh machine
- **WHEN** the recorder has never run
- **THEN** `findings`, `incidents` and `analysis` are absent — not empty — so a consumer can tell "nothing found" from "nothing recorded"

### Requirement: Finding shape
`Finding.severity` SHALL be one of `info | warning | critical` (string); `evidence` SHALL be a non-empty string array; `title`, `explanation`, `recommendation` strings; `confidence` optional string.

#### Scenario: Windows power finding
- **WHEN** ConnectionDoctor emits "Power supply under-served"
- **THEN** its `evidence` names the measured discharge and the AC state, and `severity` is a string, not a number

### Requirement: Incident shape
`Incident` SHALL carry `start`, `end`, `devicesLost: {vidPid?, name}[]`, and optional `rootEvent`, `sharedParent` (node id), `power.peakDischargeMilliwatts`.

#### Scenario: Grouped loss on macOS
- **WHEN** several USB devices behind one hub vanish together
- **THEN** the incident lists each with its vidPid and names the hub as `sharedParent`
