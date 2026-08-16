## ADDED Requirements

### Requirement: Identical MCP tool set on both platforms
Both binaries SHALL serve MCP over stdio under server name `connectiondoctor` with the tools, input schemas and result shapes defined in `docs/mcp.md`, sourced from `docs/mcp-tools.json`.

#### Scenario: tools/list on either OS
- **WHEN** an agent calls `tools/list`
- **THEN** it receives `connection_probe`, `connection_diagnose`, `connection_incidents`, `connection_diff`, `connection_diagram` with byte-identical descriptions and input schemas on macOS and Windows

### Requirement: Results are Contract v1 shapes
Every tool result SHALL be JSON that validates against `docs/schema-v1.md`: `connection_probe` returns the envelope; `connection_diagnose` returns `{findings: Finding[]}`; `connection_incidents` returns `{incidents: Incident[]}`; `connection_diff` returns `{findings, missing, added, baselineCapturedAt}` with contract nodes; `connection_diagram` returns an Excalidraw document.

#### Scenario: Evidence is mandatory
- **WHEN** `connection_diagnose` returns a finding
- **THEN** it has a non-empty `evidence[]` and a string `severity` of `info` | `warning` | `critical`

### Requirement: Honest about missing history
History-based tools SHALL return an empty list and a `note` explaining that the recorder has not run when no recording exists, and SHALL never return a "no faults" verdict without data.

#### Scenario: Fresh machine
- **WHEN** `connection_diagnose` is called before `install` has ever run
- **THEN** the result is `{findings: [], note: "…recorder has not run…"}` and `isError` is false

### Requirement: Read-only
No MCP tool SHALL change machine state, start or stop the recorder, or write outside the data directory.

#### Scenario: Agent asks to install
- **WHEN** an agent looks for a tool to register the collector at login
- **THEN** none exists; the description of `connection_probe` points at the CLI `install` verb

### Requirement: Deprecated macOS names for one release
macOS SHALL keep `tb_probe`, `tb_diagnose`, `tb_incidents`, `tb_diagram`, `tb_contract` as aliases for one release, listed after the canonical tools with "(deprecated)" in the description and returning the new result shapes.

#### Scenario: Old registration
- **WHEN** an agent configured before the rename calls `tb_diagnose`
- **THEN** it receives the same result as `connection_diagnose`
