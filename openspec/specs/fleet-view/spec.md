# fleet-view Specification

## Purpose
TBD - created by archiving change add-react-dashboard. Update Purpose after archive.
## Requirements
### Requirement: Multi-host overview
The system SHALL display multiple hosts' latest state side by side, each labelled with host name, power source, and device count, from independently loaded sources.

#### Scenario: Mixed platforms
- **WHEN** envelopes from a macOS and a Windows producer are loaded
- **THEN** both render with identical semantics and no platform-specific fields leak into the shared UI

### Requirement: Detect device migrations between hosts
The system SHALL detect a `deviceRemoved` on one host followed within 120 seconds by `deviceAdded` of the same vidPid on another host and render it as a migration (source → destination) rather than as unrelated incidents.

#### Scenario: KVM switch moves a branch
- **WHEN** a hub and its child devices leave host A and appear on host B in one window with a shared parent on both sides
- **THEN** the UI shows a single branch migration naming the shared hub, not one migration per device

#### Scenario: Duplicate hardware
- **WHEN** the same vidPid exists on two hosts simultaneously
- **THEN** adds and removes that cannot be count-matched are shown as independent events, not forced into a migration

