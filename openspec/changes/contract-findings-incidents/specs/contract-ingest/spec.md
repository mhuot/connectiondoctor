## ADDED Requirements

### Requirement: Parse optional findings, incidents and analysis
The dashboard SHALL parse `findings[]`, `incidents[]` and `analysis{}` when present, rejecting findings without evidence or with an unknown severity, and SHALL treat their absence as "not reported by this collector", distinct from empty arrays.

#### Scenario: Producer without analysis
- **WHEN** an envelope has no `analysis` field
- **THEN** the host loads normally and the findings panel explains that this collector has no recording yet
