## ADDED Requirements

### Requirement: Cross-platform rules are written and obeyed
`docs/schema-v1.md` SHALL state, and both producers SHALL implement: `tunneled` requires a `thunderbolt` ancestor; `displays[]` is canonical and display nodes carry `attachedTo`; `power.adapter` is present whenever `externalConnected`; `hub` is by `usbClass`/children not name; `linkDown`/`linkUp` may be poll-derived and carry `source`; the two deficit thresholds; `adapterChanged` vs `acChanged`; trim forces `fullSnapshot`; `host.arch` enum; timestamps with offset; `tb.route` integer.

#### Scenario: Windows laptop on a plain charger
- **WHEN** ConnectionDoctor emits the envelope
- **THEN** `power.adapter` is present (`identifiesItself` false if unknown) and the dashboard shows an adapter, not "nothing is supplying power"

#### Scenario: Monitor on either OS
- **WHEN** an LG UltraWide with a hub is attached
- **THEN** both producers emit it in `displays[]` with `attachedTo`, and the dashboard merges it with its hub identically on both

### Requirement: Stable, scoped endpoint and unit identity
Producers SHALL emit `host.id` as an opaque random per-installation identifier persisted in the data directory — never derived from a hardware identifier — and MAY emit `node.unitKey` as an HMAC of the device serial under a per-installation secret; the raw serial SHALL never appear in any document. Consumers SHALL key hosts on `host.id` when present and SHALL NOT claim two same-VID:PID nodes are one unit without matching `unitKey`. Redacted exports SHALL replace `host.id` with an export-scoped pseudonym and omit `unitKey`.

#### Scenario: Renamed Mac
- **WHEN** a host's name changes between two recordings with the same `host.id`
- **THEN** the dashboard shows one host with continuous history

#### Scenario: Two identical docks on one host
- **WHEN** two docks with the same VID:PID and different `unitKey` are attached to one endpoint
- **THEN** the topology and diff treat them as two units; without `unitKey` it says "same model, unit unknown"

#### Scenario: Two shared bundles
- **WHEN** the same machine produces two `contract --redact` exports
- **THEN** their `host.id` values differ and neither contains `unitKey` or `platform{}`, so a recipient cannot link them

#### Scenario: Reinstall
- **WHEN** the data directory is removed and the collector runs again
- **THEN** a new `host.id` is generated and the documentation says history before it is a different endpoint

### Requirement: Excalidraw export exists once
The topology-to-Excalidraw export SHALL be implemented once, in the dashboard's domain layer, and shipped to the collectors as one embedded script; the CLI `excalidraw` verb and MCP `connection_diagram` on both platforms SHALL produce their document by running that script, never by a native reimplementation.

#### Scenario: Same file from either binary
- **WHEN** the same fixture is exported on macOS and Windows with the same style
- **THEN** the documents are identical (deterministic seeds)
