# timeline-view Specification

## Purpose
TBD - created by archiving change add-react-dashboard. Update Purpose after archive.
## Requirements
### Requirement: Charts over recorded state
The system SHALL chart link state (step interpolation), power (adapter rating and battery rate), and device count over a selectable window.

#### Scenario: Link state rendering
- **WHEN** the link was down between two samples
- **THEN** the chart steps between states rather than interpolating a slope that implies intermediate states

### Requirement: Mark root events only
The system SHALL mark `linkDown` (and other root-classified) events as timeline rules and SHALL NOT mark fallout events individually.

#### Scenario: Burst of fallout
- **WHEN** one linkDown produces dozens of deviceRemoved events
- **THEN** the timeline shows one root mark, and the incident groups the removals

### Requirement: Findings with evidence
The system SHALL render findings as severity, title, explanation, evidence list, and optional recommendation; findings without evidence SHALL be rejected at ingest.

#### Scenario: Deficit finding
- **WHEN** a finding reports a power deficit
- **THEN** the measured battery contribution appears in its evidence list

