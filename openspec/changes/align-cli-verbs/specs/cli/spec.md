## ADDED Requirements

### Requirement: macOS gains the resident-process verbs
`tbdoctor` SHALL implement `install`, `uninstall`, `status`, `ui` and `collect` per `docs/cli.md`, using `SMAppService` for login registration.

#### Scenario: Install on a Mac
- **WHEN** `tbdoctor install` runs
- **THEN** TBDoctor appears in System Settings → Login Items, the collector is running, and `tbdoctor status` exits 0

#### Scenario: Headless Mac mini
- **WHEN** `tbdoctor collect` runs over SSH with no display session
- **THEN** it records to the data directory without starting the menu bar UI, writing `heartbeat.json` on every sample

#### Scenario: Second collector
- **WHEN** `tbdoctor collect` runs while the menu-bar app already holds the store lock
- **THEN** it prints "another collector owns <dir> (PID n)" on stderr and exits 3 without writing anything

#### Scenario: Status codes
- **WHEN** `tbdoctor status` runs
- **THEN** it exits 0 only if the heartbeat's process is alive and its last sample is at most 3× the sample interval old; 1 when not running, stale or absent; 3 when the lock is held but no live heartbeat exists

#### Scenario: Install waits for proof
- **WHEN** `tbdoctor install` returns 0
- **THEN** a heartbeat newer than the install time exists — registration alone is not success

### Requirement: macOS gains baseline and diff
`tbdoctor baseline save [path]` SHALL write a v1 envelope and `tbdoctor diff [baseline]` SHALL compare the current envelope against it by cross-platform identity (vidPid + parent), reporting findings, missing and added nodes.

#### Scenario: Dock moved between Macs
- **WHEN** a baseline saved on one Mac is passed to `diff` on another with the same dock
- **THEN** devices are matched by vidPid+parent, not by locationID, so the same peripherals do not appear as both missing and added

### Requirement: Windows gains version, --json and aliases
`connectiondoctor` SHALL implement `version`, `--json` on `probe`/`tree`/`report`/`diff`, treat `snapshot` as an alias of `contract`, honour `CONNECTIONDOCTOR_DIR`, and list `excalidraw` (with an interim message until the shared export exists).

#### Scenario: Version parity
- **WHEN** `version --json` runs on either binary
- **THEN** it returns `{name, version, commit, dashboard}` — the same object the envelope's `producer` field carries

### Requirement: Port reuse requires product identity
`ui` on both platforms SHALL reuse an existing server on the port only when its response carries the `Server: connectiondoctor/…` (or `tbdoctor/…`) header, SHALL start `serve` when the port is free, and SHALL refuse with a clear message and exit 1 when another service holds the port.

#### Scenario: A dev server on 8787
- **WHEN** an unrelated process answers 200 on port 8787 and `ui` runs
- **THEN** nothing is opened; the message names the port and suggests `serve <port>`

### Requirement: Help reads the same
Both binaries' `help` SHALL list the same verbs in the same order with the same one-line descriptions, generated from one table.

#### Scenario: Diff the help
- **WHEN** `tbdoctor help` and `connectiondoctor help` are diffed after replacing the binary name
- **THEN** the verb list is identical
