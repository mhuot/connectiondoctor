# Tasks: align-cli-verbs
- [ ] 1.1 macOS: verb dispatch + `--flag` aliases with stderr notice; remove `--inspect` with pointer
- [ ] 1.2 macOS: heartbeat.json on every sample; `Collector.start() throws` on lock conflict; `install` (register + launch + wait for heartbeat), `uninstall`, `status` (0/1/3), `collect` (exit 3 on conflict) — issue #18
- [ ] 1.3 macOS: `baseline save`, `diff` over v1 envelopes; `--json` on probe/tree/report/diff
- [ ] 1.4 macOS: MCP tool renames + `tb_*` aliases; `docs/mcp-tools.json` embedded
- [ ] 1.5 Windows: `version`, `--json` forms, `snapshot`→`contract` alias, ~~`CONNECTIONDOCTOR_DIR`~~ (landed with #27's identity work — tests need directory isolation), `excalidraw` placeholder
- [ ] 1.5a Windows: integration test that starts `serve` on an ephemeral port and asserts the exact advertised `/`, `/contract`, `/events` URLs return 200 (needs `ContractServer.Run` to expose a stop handle) — issue #39
- [ ] 1.5b Both: `Server: <product>/<version>` header on every HTTP response; `ui` reuses a port only on that header, refuses otherwise (Windows `IsAlreadyServing` fixed) — issue #21
- [ ] 1.6 Both: `help` from one table; READMEs updated; `docs/cli.md` status column flipped
- [ ] 1.7 Both: `version` verb + envelope `producer{}` (this change owns them; `release-pipeline` passes VERSION / `-p:Version`) — issue #19
