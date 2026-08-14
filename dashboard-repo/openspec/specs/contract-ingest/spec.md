# contract-ingest Specification

## Purpose
TBD - created by archiving change add-live-sources. Update Purpose after archive.
## Requirements
### Requirement: Load from a collector endpoint
The system SHALL fetch `/contract` and `/events` from a user-supplied base URL, ingesting both through the same validation as files, and SHALL surface fetch or validation failures without discarding already-loaded hosts.

#### Scenario: Healthy collector
- **WHEN** a base URL serving a v1 contract and events stream is added
- **THEN** the host appears with its envelope and events, labelled with the envelope's capturedAt and its origin URL

#### Scenario: Endpoint down or invalid
- **WHEN** the fetch fails or the payload is not a valid v1 envelope
- **THEN** an error names the URL and cause, and existing hosts remain loaded

### Requirement: Refresh on demand
The system SHALL re-fetch all HTTP-origin hosts on user request, replacing each host's envelope and events atomically per host.

#### Scenario: One host offline during refresh
- **WHEN** refresh is invoked and one collector is unreachable
- **THEN** reachable hosts update, the unreachable one keeps its previous data, and the failure is surfaced

### Requirement: Parse Connection Contract v1 envelopes
The system SHALL parse JSON documents with `schema: "connection-contract/v1"` into typed models, tolerating unknown fields and absent optional fields per the contract's additive-only rule.

#### Scenario: Valid envelope
- **WHEN** a v1 envelope from any producer (TBDoctor or ConnectionDoctor) is loaded
- **THEN** host, power, nodes, and displays are available as typed data, and unknown fields are preserved but ignored

#### Scenario: Wrong or missing schema field
- **WHEN** a document lacks `schema` or carries an unknown version
- **THEN** ingest fails with a message naming the expected version, and no partial data reaches the views

### Requirement: Reconstruct hierarchy from parentId
The system SHALL build the node tree solely from `id`/`parentId`, treating nodes with missing or unresolvable parents as roots.

#### Scenario: Orphaned parent reference
- **WHEN** a node's `parentId` does not match any node in the envelope
- **THEN** the node is attached at the root level and flagged in diagnostics, not dropped

### Requirement: Ingest event streams
The system SHALL parse v1 events JSONL, skipping unparseable lines while counting them, and SHALL treat `fullSnapshot` events as sync points that reset accumulated state.

#### Scenario: Corrupt line in stream
- **WHEN** one line of a JSONL stream fails to parse
- **THEN** remaining lines still load and the skip count is surfaced

### Requirement: Label recorded data as recorded
The system SHALL display fixture or file-loaded data with its capture timestamp and SHALL NOT show live-freshness indicators for it.

#### Scenario: Loading a file
- **WHEN** a contract file is loaded from disk
- **THEN** views show "recorded <capturedAt>" rather than an age that implies staleness

