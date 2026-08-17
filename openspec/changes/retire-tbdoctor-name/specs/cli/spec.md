## ADDED Requirements

### Requirement: One product name on both platforms
The macOS collector SHALL ship as `ConnectionDoctor.app` with executable `ConnectionDoctor` and bundle identifier `net.mhuot.connectiondoctor`, and its CLI SHALL be named `connectiondoctor`, so that documentation names one binary rather than two.

#### Scenario: Reading the CLI documentation
- **WHEN** a reader follows `docs/cli.md` on either platform
- **THEN** every verb is shown for one command name, with no per-platform binary aliasing

### Requirement: The rename preserves data and PATH commands, and names what it breaks
The collector SHALL migrate an existing `~/Library/Application Support/TBDoctor` directory to the new location on first run when the new directory does not exist, SHALL continue to honour `TBDOCTOR_DIR`, and SHALL provide a `tbdoctor` command alias **on PATH** for one release that prints a deprecation notice on stderr. Commands naming the old application bundle by absolute path SHALL NOT be preserved — the bundle no longer exists — and the release notes SHALL state that an MCP registration made against the old path must be re-registered, with the exact command.

#### Scenario: Upgrading a machine with recorded history
- **WHEN** the new build runs on a machine whose history and baseline live under the old directory
- **THEN** they are moved to the new directory and remain readable — findings, incidents and the baseline are unchanged

#### Scenario: Both directories exist
- **WHEN** old and new directories are both present
- **THEN** the new one is used, the old one is left untouched, and nothing is merged or deleted

#### Scenario: An old command on PATH
- **WHEN** a script invokes `tbdoctor` from PATH
- **THEN** it runs, prints a one-line notice naming the new command, and behaves identically

#### Scenario: An MCP registration against the old bundle path
- **WHEN** a registration points at `…/TBDoctor.app/Contents/MacOS/TBDoctor`
- **THEN** it fails because that bundle is gone — an intentional break, stated in the release notes with its one-line fix, not something the PATH alias pretends to cover
