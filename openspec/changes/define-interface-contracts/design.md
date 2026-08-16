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
