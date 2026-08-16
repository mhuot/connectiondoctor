## ADDED Requirements

### Requirement: Producer identity in the envelope
The v1 envelope MAY carry `producer: {name: "tbdoctor" | "connectiondoctor", version: string, commit?: string, dashboard?: string}`; consumers SHALL tolerate its absence.

#### Scenario: Fleet of mixed versions
- **WHEN** hosts running different releases are loaded in the dashboard
- **THEN** each host's version is visible in the fleet view, so a drift report can name the build
