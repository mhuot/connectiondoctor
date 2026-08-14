## ADDED Requirements

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
