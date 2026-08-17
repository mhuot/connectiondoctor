## ADDED Requirements

### Requirement: Parse optional findings, incidents and analysis
The dashboard SHALL parse `findings[]`, `incidents[]` and `analysis{}` when present, rejecting findings without evidence or with an unknown severity, and SHALL treat their absence as "not reported by this collector", distinct from empty arrays.

#### Scenario: Producer without analysis
- **WHEN** an envelope has no `analysis` field
- **THEN** the host loads normally and the findings panel explains that this collector has no recording yet

### Requirement: Per-host freshness and history completeness
The dashboard SHALL track `/contract` and `/events` success independently per host with the time of last successful contact, SHALL surface skipped or corrupt event lines and fetch failures on the affected host, and SHALL classify each host as live, stale, offline, envelope-only or history-incomplete using documented thresholds. Retained stale data SHALL remain visible but never read as current.

#### Scenario: Events unreachable, envelope fine
- **WHEN** `/contract` succeeds and `/events` fails on refresh
- **THEN** the host is marked envelope-only, the failure is shown on that host, and the timeline says history is unavailable rather than showing an empty, healthy-looking timeline

#### Scenario: Corrupt lines
- **WHEN** the events stream contains lines the parser skips
- **THEN** the count is shown on that host and the host is marked history-incomplete

#### Scenario: Recovery
- **WHEN** the next refresh succeeds
- **THEN** the host returns to live and the incomplete markers clear
