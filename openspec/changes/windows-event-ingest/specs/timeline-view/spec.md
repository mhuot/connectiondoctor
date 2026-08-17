## ADDED Requirements

### Requirement: Root-event kinds and sources
The timeline SHALL treat `linkDown` and `portError` as root events, SHALL rank `source: kernel` above `notification` above `poll` when attributing an incident, and SHALL label the source of a root event.

#### Scenario: Windows incident with a kernel root
- **WHEN** a Windows host's incident carries a `portError` with `source: kernel` followed by device removals
- **THEN** the incident is attributed to the port error and the fallout is grouped under it, as on macOS
