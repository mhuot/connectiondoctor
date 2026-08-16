## ADDED Requirements

### Requirement: Windows serves MCP
`ConnectionDoctor.exe mcp` SHALL run an MCP server on stdio implementing the tool set in `docs/mcp.md`, with `tools/list` served from the embedded `docs/mcp-tools.json`.

#### Scenario: Register and probe from Claude Code
- **WHEN** `claude mcp add connectiondoctor -- ConnectionDoctor.exe mcp` is configured and the agent calls `connection_probe`
- **THEN** it receives the current v1 envelope for the machine

#### Scenario: Diff from an agent
- **WHEN** a baseline was saved with `baseline save` and the agent calls `connection_diff`
- **THEN** it receives `{findings, missing, added, baselineCapturedAt}` matching `diff --json`

#### Scenario: Diagram not yet available
- **WHEN** `connection_diagram` is called before the shared Excalidraw export exists
- **THEN** the result is `isError: true` with a message naming the dashboard's export as the interim path — the tool is listed, not silently absent
