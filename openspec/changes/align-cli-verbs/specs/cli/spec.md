## ADDED Requirements

### Requirement: macOS gains the resident-process verbs
`tbdoctor` SHALL implement `install`, `uninstall`, `status`, `ui` and `collect` per `docs/cli.md`, using `SMAppService` for login registration.

#### Scenario: Install on a Mac
- **WHEN** `tbdoctor install` runs
- **THEN** TBDoctor appears in System Settings → Login Items, the collector is running, and `tbdoctor status` exits 0

#### Scenario: Headless Mac mini
- **WHEN** `tbdoctor collect` runs over SSH with no display session
- **THEN** it records to the data directory without starting the menu bar UI, and refuses with a clear message if another collector holds the store lock

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

### Requirement: Help reads the same
Both binaries' `help` SHALL list the same verbs in the same order with the same one-line descriptions, generated from one table.

#### Scenario: Diff the help
- **WHEN** `tbdoctor help` and `connectiondoctor help` are diffed after replacing the binary name
- **THEN** the verb list is identical
