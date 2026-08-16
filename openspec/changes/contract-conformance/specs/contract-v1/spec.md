## ADDED Requirements

### Requirement: Cross-platform rules are written and obeyed
`docs/schema-v1.md` SHALL state, and both producers SHALL implement: `tunneled` requires a `thunderbolt` ancestor; `displays[]` is canonical and display nodes carry `attachedTo`; `power.adapter` is present whenever `externalConnected`; `hub` is by `usbClass`/children not name; `linkDown`/`linkUp` may be poll-derived and carry `source`; the two deficit thresholds; `adapterChanged` vs `acChanged`; trim forces `fullSnapshot`; `host.arch` enum; timestamps with offset; `tb.route` integer.

#### Scenario: Windows laptop on a plain charger
- **WHEN** ConnectionDoctor emits the envelope
- **THEN** `power.adapter` is present (`identifiesItself` false if unknown) and the dashboard shows an adapter, not "nothing is supplying power"

#### Scenario: Monitor on either OS
- **WHEN** an LG UltraWide with a hub is attached
- **THEN** both producers emit it in `displays[]` with `attachedTo`, and the dashboard merges it with its hub identically on both

### Requirement: Excalidraw export exists once
The topology-to-Excalidraw export SHALL be implemented once, in the dashboard's domain layer, and shipped to the collectors as one embedded script; the CLI `excalidraw` verb and MCP `connection_diagram` on both platforms SHALL produce their document by running that script, never by a native reimplementation.

#### Scenario: Same file from either binary
- **WHEN** the same fixture is exported on macOS and Windows with the same style
- **THEN** the documents are identical (deterministic seeds)
