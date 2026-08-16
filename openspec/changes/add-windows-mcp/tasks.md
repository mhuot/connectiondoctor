# Tasks: add-windows-mcp
- [ ] 1.1 `McpServer.cs`: stdio JSON-RPC loop, `initialize`/`tools/list`/`tools/call`/`ping`; `mcp` verb in `Program.cs`
- [ ] 1.2 Embed `docs/mcp-tools.json`; test: embedded == repo copy
- [ ] 1.3 `connection_probe`, `connection_incidents`, `connection_diff` over existing core
- [ ] 1.4 `Finding` gains `Evidence`; `connection_diagnose` returns schema findings with evidence
- [ ] 1.5 `connection_diagram` present; returns `isError` + note until the dashboard export exists (see design)
- [ ] 1.6 Tests: request/response golden files for each tool over the shared fixtures
- [ ] 1.7 README + `docs/mcp.md` status flip; `claude mcp add` line
