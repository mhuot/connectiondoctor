# Tasks: add-windows-mcp
- [x] 1.1 `McpServer.cs`: stdio JSON-RPC loop, `initialize`/`tools/list`/`tools/call`/`ping`; `mcp` verb in `Program.cs`
- [x] 1.2 Embed `docs/mcp-tools.json`; test: embedded == repo copy
- [x] 1.3 `connection_probe`, `connection_incidents`, `connection_diff` over existing core
- [x] 1.4 `Finding` gains `Evidence`; `connection_diagnose` returns schema findings with evidence
- [x] 1.5 `connection_diagram` present; returns `isError` + note until the dashboard export exists (see design)
- [~] 1.6 Tests: protocol round trip over in-memory streams, embedded-json parity, document serialization over synthetic snapshots (McpServerTests.cs); golden files over the shared fixtures follow when contract-conformance adds docs/fixtures/
- [x] 1.7 README + `docs/mcp.md` status flip; `claude mcp add` line
- [x] 1.8 Hardening from review (#48): -32700/-32600 answered and served through; explicit `id: null` answered; bad-typed method survives; protocol version negotiated from a supported list; `sharedParent` only on resolvable common ancestry (pre-incident snapshot); `incidents[].power.peakDischargeMilliwatts`; diagnose `note` never success-shaped
