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
- **WHEN** the host is history-incomplete or envelope-only
- **THEN** the panel says "unknown — history incomplete" with the reason, never "no findings"

### Requirement: Timeline prefers producer incidents
The Timeline SHALL use `incidents[]` from the envelope when present and its own stitching otherwise, and SHALL label which it is showing.

#### Scenario: Mixed fleet
- **WHEN** one host sends incidents and another does not
- **THEN** each host's timeline is labelled "from collector" or "derived by dashboard" accordingly
