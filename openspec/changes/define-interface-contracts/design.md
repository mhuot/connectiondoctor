# Design: define-interface-contracts

## Decision: verbs, not flags
Windows already uses subcommands (`probe`, `serve`, `install`), which is the
conventional shape for a multi-function binary and the one every other tool an
agent or person is likely to have used. macOS moves to it and keeps `--verb`
forms as one-release deprecated aliases so `claude mcp add … --mcp`
registrations and shell history keep working with a stderr notice.

## Decision: `--json` means Contract v1, always
No verb gets a private JSON shape. `probe --json` and `tree --json` are the
envelope; `report --json` is `{findings[], incidents[]}`; `diff --json` is
`{findings[], missing[], added[]}` with nodes as contract nodes. This is what
makes the CLI, MCP and dashboard three views of one model, and it is what the
conformance fixtures can test.

## Decision: MCP results are contract shapes; names are platform-neutral
`tb_*` names encode the macOS product and the assumption that Thunderbolt is
the fault. `connection_*` matches the umbrella name and the README's own
argument. Five tools: `probe`, `diagnose`, `incidents`, `diff`, `diagram`.
`tb_contract` merges into `connection_probe` (it already returned the envelope);
`connection_diff` is new on both and exposes the Windows baseline story to agents.

## Decision: one source file for tool metadata
`docs/mcp-tools.json` holds `{name, description, inputSchema}` per tool. The
Swift and C# servers embed it (SwiftPM resource / EmbeddedResource) and serve
`tools/list` from it. A test on each side asserts the embedded copy equals the
repo copy. Descriptions are prose written for the model, kept from today's
`MCPServer.swift`, which are good.

## Decision: retire `--inspect`, alias `snapshot`
`--inspect <file>` opened a native window on a recording; the dashboard's file
drop does this on both platforms already and is where recordings should be
viewed. Windows `snapshot` predates the contract; it becomes an alias of
`contract [path]` and its native JSON is retired once `baseline` reads
envelopes (`contract-conformance`).

## Exit codes
`0` ok · `1` error/usage · `2` critical finding present (`report`, `diff`).
Windows `diff` already returns 2; generalised.

## Data directory
`CONNECTIONDOCTOR_DIR` overrides on both. macOS keeps `TBDOCTOR_DIR` as an
alias because fixtures and the MCP registration line in the README use it.

## Second-reader review

The 2026-08-16 code-and-proposal review accepted the interface direction and
recorded implementation or specification gaps as follow-ups:

- [#18](https://github.com/mhuot/connectiondoctor/issues/18) defines the macOS
  install, heartbeat, status, and collector-lock lifecycle before CLI alignment.
- [#19](https://github.com/mhuot/connectiondoctor/issues/19) assigns version
  ownership once and sequences Windows MCP diff after contract conformance.
- [#20](https://github.com/mhuot/connectiondoctor/issues/20) makes the CLI and
  MCP wrapper document shapes normative and testable.
- [#21](https://github.com/mhuot/connectiondoctor/issues/21) gives port reuse a
  product identity check instead of accepting any HTTP success on port 8787.
- [#22](https://github.com/mhuot/connectiondoctor/issues/22) prevents dashboard
  version injection failures from being ignored by release builds.
- [#23](https://github.com/mhuot/connectiondoctor/issues/23) reconciles the
  documented macOS release architecture with the implemented build.
