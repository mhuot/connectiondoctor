# Proposal: define-interface-contracts

## Why
The dashboard is genuinely one implementation on both platforms because two
written contracts hold it there: `docs/schema-v1.md` (the data) and
`docs/embedding.md` (how a collector serves it). The other two doors — the CLI
and the MCP server — have no such contract, and it shows: macOS uses `--flags`,
Windows uses verbs; macOS has five `tb_*` MCP tools, Windows has none; the
verb sets differ (`baseline`/`diff`/`install`/`status` only on Windows,
`excalidraw`/`inspect` only on macOS). "Same look and feel on both" cannot be
held without something to hold it to.

## What
- Write `docs/cli.md`: one verb set, conventions (`--json` = contract shapes,
  exit codes, data directory, stderr/stdout), text output order, retired verbs.
- Write `docs/mcp.md`: server name, platform-neutral tool names
  (`connection_*`), input schemas, result shapes (contract shapes only),
  compatibility table for today's `tb_*` names.
- Add `docs/mcp-tools.json`: the single source of tool names, descriptions and
  input schemas that both binaries embed, so descriptions cannot drift.
- Record the architecture decision (shared in-process core, not
  CLI-as-substrate) in `docs/architecture.md`.

This change is documentation and one JSON file. Code changes follow in
`add-windows-mcp` and `align-cli-verbs`.

## Non-goals
Implementing anything; changing Contract v1 (see `contract-findings-incidents`);
deciding text output byte-for-byte (the spec fixes order and facts, golden
tests in `contract-conformance` fix bytes).

## Impact
New capabilities `cli` and `mcp` (specs), `docs/`. No code.
