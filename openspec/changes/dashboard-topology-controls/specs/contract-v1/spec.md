## ADDED Requirements

### Requirement: Producers classify built-in devices
The v1 envelope MAY carry `nodes[].builtIn: boolean`, set by the producer from its own classification (Windows `DeviceFilters`; macOS internal keyboard/trackpad/camera/ambient-light and the internal display's hub); consumers SHALL treat absence as "unknown", not "external", and SHALL NOT infer built-in status from names.

#### Scenario: Consistent across snapshots
- **WHEN** the dashboard filters built-ins and a `fullSnapshot` sync point is read
- **THEN** the same nodes are hidden, because filtering happens on the flag the producer wrote, never on a request-time option
