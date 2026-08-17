## ADDED Requirements

### Requirement: Shared fixtures with expected analysis
The repository SHALL hold recorded fixtures (`docs/fixtures/<case>/`) with an `expected.json` of findings and incidents, and each analysis implementation (Swift, C#, TypeScript) SHALL have a conformance test that produces equal results for every case.

#### Scenario: New drift bug
- **WHEN** a case is added where the two platforms disagree
- **THEN** at least one conformance test fails until the disagreeing implementation is fixed

#### Scenario: Same rig, either OS
- **WHEN** the Surface-dock-chain fixture is analysed on all three
- **THEN** the same findings appear with the same severities and the same incident count and shared parents

### Requirement: Documents are machine-validated
JSON Schema files under `docs/schema/v1/` SHALL define the envelope, report and diff documents, and every conformance test and the dashboard parser tests SHALL validate their inputs and outputs against them.

#### Scenario: Renamed key
- **WHEN** a producer emits `baselineCaptured` instead of `baselineCapturedAt` in a diff document
- **THEN** its conformance test fails on schema validation, not on a downstream null

### Requirement: Analysis consumes Contract v1
Findings and incidents SHALL be computed from the v1 envelope and event stream, not from platform-native models, so that fixtures are the only input a conformance test needs.

#### Scenario: Analysing another machine's recording
- **WHEN** a Windows recording is passed to the macOS `report` verb via `CONNECTIONDOCTOR_DIR`
- **THEN** it analyses it and produces the same findings the Windows binary does
