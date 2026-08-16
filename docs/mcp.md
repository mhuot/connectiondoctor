# MCP server

> **Status: proposed** — introduced by `openspec/changes/define-interface-contracts`;
> Windows implementation in `add-windows-mcp`; macOS renames in `align-cli-verbs`.
> Today macOS serves the same tools under `tb_*` names via `--mcp`; Windows has
> no MCP server yet.

Both binaries are MCP servers over stdio (JSON-RPC 2.0, MCP `2024-11-05`
protocol version or later). The server name is `connectiondoctor` on both
platforms; the tool set, names, input schemas and result shapes are identical,
so an agent's instructions written against one machine work against the other.

```sh
claude mcp add connectiondoctor -- /path/to/TBDoctor.app/Contents/MacOS/TBDoctor mcp   # macOS
claude mcp add connectiondoctor -- C:\path\to\ConnectionDoctor.exe mcp                  # Windows
```

## Principles

- **Results are Contract v1 shapes.** Every tool returns JSON that validates
  against [`schema-v1.md`](schema-v1.md): the envelope, `Finding[]`,
  `Incident[]`. No tool invents a third shape; the dashboard, the CLI's
  `--json` and the MCP tools are three views of one data model.
- **Read-only.** No tool changes machine state, starts the recorder, or writes
  outside the data directory. `install`/`uninstall` are CLI-only on purpose.
- **Honest about absence.** When the recorder has never run, history-based
  tools return an empty list plus a `note` field saying so, rather than
  fabricating "no faults".
- **Platform-neutral names.** `connection_*`, never `tb_*`. macOS keeps the
  `tb_*` names as deprecated aliases for one release, listed last in
  `tools/list` with "(deprecated)" in the description.

## Tools

| Tool | Input | Returns | Use it for |
|---|---|---|---|
| `connection_probe` | `{}` | v1 **envelope** (current state) | "what is plugged in right now", "is it actually charging" |
| `connection_diagnose` | `{ hours?: number = 6 }` | `{ findings: Finding[], note?: string }` ranked critical → info, each with mandatory `evidence[]` | "my dock keeps disconnecting", root-cause questions |
| `connection_incidents` | `{ hours?: number = 24, limit?: number = 20 }` | `{ incidents: Incident[], note?: string }` newest first | "when and how often did it happen" |
| `connection_diff` | `{ baseline?: string }` (path; default = saved baseline) | `{ findings: Finding[], missing: Node[], added: Node[], baselineCapturedAt: string }` | "what is missing now that was there when the desk worked" |
| `connection_diagram` | `{ style?: "cascade" \| "topDown" \| "flow" = "cascade", full?: boolean = false }` | Excalidraw document (JSON) | "show me / share how this is wired" |

Descriptions are written for the model, in the style of the existing
`tb_probe`/`tb_diagnose` text: what question the tool answers, not how it is
implemented. Both binaries embed the same description strings, generated from
one source file (`docs/mcp-tools.json`, added by `define-interface-contracts`),
so they cannot drift.

Result content is returned as one `text` content block containing the JSON;
`isError: true` with a plain-English message on failure. Tools never block on a
probe longer than 10 s; a slow enumeration returns what it has with a `note`.

## Compatibility with today's macOS tools

| Today (`tb_*`) | Becomes | Note |
|---|---|---|
| `tb_probe` | `connection_probe` | today returns text; becomes the envelope (the text form stays on the CLI) |
| `tb_contract` | `connection_probe` | merged — it was already the envelope |
| `tb_diagnose` | `connection_diagnose` | same input; result gains the schema `Finding` shape (string severities, evidence mandatory) |
| `tb_incidents` | `connection_incidents` | same input; result gains the schema `Incident` shape |
| `tb_diagram` | `connection_diagram` | same |
| — | `connection_diff` | new on both; the Windows baseline story exposed to agents |
