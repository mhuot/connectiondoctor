## ADDED Requirements

### Requirement: Sub-poll transitions are recorded
The Windows collector SHALL ingest OS device-interface notifications with their original timestamps so that a device reset that fails and recovers within one poll interval is recorded as an ordered removed/added pair, and SHALL keep polling for reconciliation and startup recovery.

#### Scenario: Three-second reset
- **WHEN** a hub disappears and reappears within 3 s between two identical polls
- **THEN** the events stream contains `deviceRemoved` then `deviceAdded` for that node with OS timestamps and `source: "notification"`, and an incident is visible in the dashboard

### Requirement: One transition, one event
Notification-derived and poll-derived events for the same node and kind within one poll interval SHALL be recorded once, with the notification's timestamp; ordering in the stream SHALL be by event time.

#### Scenario: Notification then poll
- **WHEN** a notification records `deviceRemoved` for a node and the next poll also observes it missing
- **THEN** exactly one `deviceRemoved` exists for that node in that interval

### Requirement: Root events only on evidence
The collector SHALL emit `linkDown`, `linkUp` and `portError` with `source: "kernel"` only when an ETW provider event supports them, and SHALL leave device loss unattributed otherwise; when no ETW session is available it SHALL say so in `analysis.coverage.reasons` rather than deriving link events from device loss.

#### Scenario: No ETW session
- **WHEN** the collector runs without the opt-in ETW session
- **THEN** incidents have no `rootEvent`, and the report notes `link-events-unavailable`
