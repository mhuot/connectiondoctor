## ADDED Requirements

### Requirement: Parse optional findings, incidents and analysis
The dashboard SHALL parse `findings[]`, `incidents[]` and `analysis{}` when present, rejecting findings without evidence or with an unknown severity, and SHALL treat their absence as "not reported by this collector", distinct from empty arrays.

#### Scenario: Producer without analysis
- **WHEN** an envelope has no `analysis` field
- **THEN** the host loads normally and the findings panel explains that this collector has no recording yet

### Requirement: Per-host contact and history quality
The dashboard SHALL track `/contract` and `/events` success independently per host with their last-success times, and SHALL derive two independent statuses: **contact** (`live` | `stale` | `offline`) and **history** (`complete` | `no-history` | `envelope-only` | `incomplete` with durable reasons: skipped lines, fetch failure, producer `coverage.complete == false`, window shorter than requested). Completeness SHALL come from the producer's `analysis.coverage`, never be inferred from the first event alone. Retained stale data SHALL remain visible but never read as current.

#### Scenario: Events unreachable, envelope fine
- **WHEN** `/contract` succeeds and `/events` fails on refresh
- **THEN** the host is marked envelope-only, the failure is shown on that host, and the timeline says history is unavailable rather than showing an empty, healthy-looking timeline

#### Scenario: Corrupt lines
- **WHEN** the events stream contains lines the parser skips
- **THEN** the count is shown on that host and the host is marked history-incomplete

#### Scenario: Recovery clears contact, not history
- **WHEN** the next refresh succeeds after an outage
- **THEN** contact returns to live; a history reason (skipped lines, trimmed coverage) clears only if the new payload proves the requested window complete — a later success cannot restore lines that were skipped or trimmed earlier

#### Scenario: New recorder vs empty stream
- **WHEN** a host has an empty but valid events stream
- **THEN** history is `incomplete` with the producer's reason (`recorder-started-inside-window`) rather than `complete`, so the timeline says "recording since <time>", not "no incidents"
