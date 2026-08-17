# cli Specification

## Purpose
One verb set, one output shape and one exit-code contract for both collector binaries (`tbdoctor`, `connectiondoctor`), as written in `docs/cli.md`. The CLI is one of three doors over the shared in-process core (see `docs/architecture.md`); it must never be the only way to reach a capability, and it must read the same on either OS.
## Requirements
### Requirement: One verb set on both binaries
The CLI SHALL expose the verb set defined in `docs/cli.md` with identical names, arguments and semantics on macOS (`tbdoctor`) and Windows (`connectiondoctor`). A verb a platform cannot implement SHALL still exist, print why on stderr and exit 1.

#### Scenario: Same verb, other platform
- **WHEN** a user who learnt `connectiondoctor report --hours 12` on Windows runs `tbdoctor report --hours 12` on macOS
- **THEN** the same sections appear in the same order (findings ranked, then incidents newest-first) and the exit code follows the same rule

#### Scenario: Deprecated flag form on macOS
- **WHEN** `tbdoctor --probe` is invoked
- **THEN** it behaves as `tbdoctor probe` and prints a one-line deprecation notice on stderr

### Requirement: `--json` emits Contract v1 shapes
Any verb that reports state SHALL accept `--json` and emit one of the documents defined in `docs/schema-v1.md` § Documents: the envelope for `probe`/`tree`/`contract`, the report document for `report`, the diff document for `diff` — validated against the JSON Schema files under `docs/schema/v1/` once `contract-conformance` adds them.

#### Scenario: Piping probe
- **WHEN** `probe --json` is run
- **THEN** stdout is exactly one v1 envelope that validates with the dashboard's parser, and any notices are on stderr

### Requirement: Exit codes carry the verdict
`report` and `diff` SHALL exit 2 when at least one `critical` finding is present, 0 otherwise; every verb SHALL exit 1 on usage or runtime error.

#### Scenario: Script gate
- **WHEN** a script runs `diff` and a critical finding exists
- **THEN** the exit code is 2 even though the text output succeeded

### Requirement: Data directory override
Both binaries SHALL honour `CONNECTIONDOCTOR_DIR` for all recorded data and baselines; macOS SHALL also honour `TBDOCTOR_DIR`.

#### Scenario: Fixture run
- **WHEN** `CONNECTIONDOCTOR_DIR` points at a directory containing recorded fixtures
- **THEN** `report`, `status` and `serve` read from it and never touch the default location

