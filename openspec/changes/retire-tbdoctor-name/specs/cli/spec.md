## ADDED Requirements

### Requirement: One product name on both platforms
The macOS collector SHALL ship as `ConnectionDoctor.app` with executable `ConnectionDoctor` and bundle identifier `net.mhuot.connectiondoctor`, and its CLI SHALL be named `connectiondoctor`, so that documentation names one binary rather than two.

#### Scenario: Reading the CLI documentation
- **WHEN** a reader follows `docs/cli.md` on either platform
- **THEN** every verb is shown for one command name, with no per-platform binary aliasing

### Requirement: The rename costs the user nothing
The collector SHALL migrate an existing `~/Library/Application Support/TBDoctor` directory to the new location on first run when the new directory does not exist, SHALL continue to honour `TBDOCTOR_DIR`, and SHALL provide a `tbdoctor` command alias for one release that prints a deprecation notice on stderr.

#### Scenario: Upgrading a machine with recorded history
- **WHEN** the new build runs on a machine whose history and baseline live under the old directory
- **THEN** they are moved to the new directory and remain readable — findings, incidents and the baseline are unchanged

#### Scenario: Both directories exist
- **WHEN** old and new directories are both present
- **THEN** the new one is used, the old one is left untouched, and nothing is merged or deleted

#### Scenario: An old command line
- **WHEN** a script or MCP registration invokes `tbdoctor`
- **THEN** it runs, prints a one-line notice naming the new command, and behaves identically
