## ADDED Requirements

### Requirement: Ranked findings with evidence
The dashboard SHALL show a Findings panel listing the active host's findings ranked critical → warning → info, then by confidence, each with title, explanation, evidence lines and recommendation, labelled with the analysis window and generation time.

#### Scenario: Power supply under-served
- **WHEN** a host's envelope contains a critical finding with three evidence lines
- **THEN** it appears first, with all three lines visible without interaction, and the panel header reads the window (e.g. "last 6 h · generated 12:04")

#### Scenario: No findings vs no recording
- **WHEN** `findings` is an empty array
- **THEN** the panel says no findings in the window; **WHEN** `analysis` is absent, it says the collector has no recording and how to start one

#### Scenario: Incomplete window
- **WHEN** the host's history status is `incomplete`, `envelope-only` or `no-history`
- **THEN** the panel says "unknown — <reason>" (from `coverage.reasons` or the fetch failure), never "no findings"

### Requirement: Baseline capture and state
The dashboard SHALL show the baseline state (no baseline / healthy / active fault / recovered since fault) and SHALL offer Capture baseline and Replace baseline actions via `POST /baseline` when the collector is reached over loopback, with replacement requiring confirmation that names the existing capture time.

#### Scenario: Missing hub branch on a Surface
- **WHEN** the LG hub branch present in the baseline is absent
- **THEN** the finding "display active but hub branch missing" appears with evidence and the recommendation to power-cycle, and the state reads active fault; when the branch returns the state reads recovered

#### Scenario: Reached over the LAN
- **WHEN** the dashboard is served from a LAN-bound collector
- **THEN** the baseline actions are shown disabled with the reason, and `POST /baseline` answers 403

### Requirement: Timeline prefers producer incidents
The Timeline SHALL use `incidents[]` from the envelope when present and its own stitching otherwise, and SHALL label which it is showing.

#### Scenario: Mixed fleet
- **WHEN** one host sends incidents and another does not
- **THEN** each host's timeline is labelled "from collector" or "derived by dashboard" accordingly
