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

### Requirement: Install composes the interfaces
`install` and `uninstall` SHALL accept `--recorder`, `--dashboard`, `--mcp`, `--cli` and `--all`, acting on exactly the components named; bare `install` SHALL install the recorder and the dashboard, and bare `uninstall` SHALL remove every component this tool installed. `uninstall` SHALL NOT delete recorded history or a saved baseline. `status` SHALL report each component's state separately.

#### Scenario: Nothing could be installed
- **WHEN** `install --mcp` runs where no agent registration command and no writable agent config exist
- **THEN** the exact registration line is printed, nothing is changed, and the command exits 1 — a script must not read "nothing happened" as success

#### Scenario: Some of it worked
- **WHEN** `install --all` installs the recorder, dashboard and CLI but cannot register with an agent
- **THEN** the successful components remain installed, the output names what did and did not happen, and the command exits 4 (partial), distinct from both success and total failure

#### Scenario: No writable system PATH, and no elevation
- **WHEN** `install --cli` runs where `/usr/local/bin` is not writable
- **THEN** the command installs to a per-user location on PATH (`~/.local/bin`, `%LOCALAPPDATA%\Microsoft\WindowsApps`), never prompts for elevation, exits 0, and — if that directory is not on the user's PATH — prints the exact line to add it

#### Scenario: An agent config that is not ours
- **WHEN** `--mcp` edits an agent's configuration file directly because no registration command exists
- **THEN** the write is atomic with a backup kept, unrelated servers and unknown keys are preserved, the changed file is named in the output, and `uninstall --mcp` later removes only the entry this installation created

#### Scenario: A headless recorder
- **WHEN** `install --recorder` runs on a machine with no display session
- **THEN** collection starts at login, nothing serves the dashboard, and `status` says recorder installed, dashboard not installed

#### Scenario: An agent-only machine
- **WHEN** `install --mcp` runs where an agent config exists
- **THEN** the MCP server is registered as `connectiondoctor`, no resident process is installed, and `status` reflects both

#### Scenario: No agent to register with
- **WHEN** `install --mcp` runs where no agent config can be found or written
- **THEN** the exact registration line is printed for the user to paste, and the command reports that nothing was installed rather than reporting success

#### Scenario: Uninstalling one door
- **WHEN** `uninstall --dashboard` runs on a machine with both installed
- **THEN** the dashboard stops being served at login, the recorder keeps recording, and recorded history and any baseline are untouched

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
