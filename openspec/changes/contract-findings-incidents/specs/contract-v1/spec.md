## ADDED Requirements

### Requirement: Envelope carries findings and incidents
Producers SHALL include optional `findings: Finding[]`, `incidents: Incident[]` and `analysis: {windowHours: number, generatedAt: string}` in the v1 envelope when recorded history exists, and SHALL omit all three when it does not. This is additive within v1.

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
