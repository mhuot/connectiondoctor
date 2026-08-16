## ADDED Requirements

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
