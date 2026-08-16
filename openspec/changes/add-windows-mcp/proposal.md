# Proposal: add-windows-mcp

## Why
The Windows binary has no MCP door. A coding agent helping with a Surface's dock
gets pasted terminal output while the same agent on a Mac queries the hardware
directly. It is the largest functional gap between the two platforms and one of
the smallest to close: TBDoctor's `MCPServer.swift` is ~250 lines of stdio
JSON-RPC with no platform dependencies.

## What
- `connectiondoctor mcp`: an MCP server on stdio implementing `docs/mcp.md`
  (`initialize`, `tools/list`, `tools/call`, `ping`), tools sourced from
  `docs/mcp-tools.json` embedded as a resource.
- `connection_probe` → `ContractV1.ToEnvelope(DeviceProbe.Capture())`.
- `connection_diagnose` → findings from recorded history (baseline-diff and
  power findings today; the ranked engine arrives with
  `contract-findings-incidents`), as schema `Finding` with `evidence[]`.
- `connection_incidents` → `IncidentStitcher.Stitch(...)` as schema `Incident`.
- `connection_diff` → `SnapshotComparer.Compare(baseline, current)`.
- `connection_diagram` → Excalidraw export (new on Windows; port of
  `ExcalidrawExport.swift` over the TS-equivalent layout, or reuse the
  dashboard's layout by embedding a small TS→JSON step — see design).
- README: `claude mcp add connectiondoctor -- ConnectionDoctor.exe mcp`.

## Non-goals
Streaming/notifications; anything beyond stdio transport; parity of the
findings *engine* (that is `contract-findings-incidents`).

## Impact
Capability `mcp`; `windows/src/ConnectionDoctor/McpServer.cs` (new),
`Program.cs` (verb), csproj (embedded `mcp-tools.json`); tests.

## Depends on
`define-interface-contracts` (tool names/schemas must be settled first).
