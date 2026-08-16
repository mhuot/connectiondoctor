# Proposal: align-cli-verbs

## Why
`docs/cli.md` now defines one verb set; neither binary matches it. macOS uses
`--flags` and lacks `baseline`, `diff`, `status`, `install`, `uninstall`,
`ui`, `collect`, `version`; Windows lacks `excalidraw`, `version` and the
`--json` forms; MCP tool names on macOS are `tb_*`.

## What
macOS (`Headless.swift`, `App.swift`, new files):
- Verb dispatch with `--verb` aliases (deprecation notice on stderr).
- `baseline save` / `diff` over v1 envelopes (compare = port of
  `SnapshotComparer` semantics, but on contract nodes keyed by vidPid+parent).
- `install` / `uninstall` via `SMAppService.mainApp` (macOS 13+); `status`
  from the recorder's heartbeat file; `ui` opens the browser (starting `serve`
  if nothing answers on the port); `collect` runs the collector headless.
- `--json` on `probe`/`tree`/`report`/`diff`; `version`.
- MCP tools renamed to `connection_*` with `tb_*` aliases (see `mcp` spec).
Windows (`Program.cs` + new):
- `excalidraw` (present, `isError`-style message until the shared export
  exists — same rule as `connection_diagram`), `version`, `--json` forms,
  `snapshot` → alias of `contract`, `CONNECTIONDOCTOR_DIR`.
Both: `help` text generated from one table so it reads identically.

## Non-goals
Ranked findings on Windows (`contract-findings-incidents`); retiring macOS
native windows (`macos-resident-serves-dashboard`); byte-exact golden tests
(`contract-conformance` supplies the fixtures; this change adds the verbs).

## Impact
Capability `cli`; macOS `Headless.swift`, `App.swift`, `Collector.swift`,
new `Baseline.swift`, `LoginItem.swift`; Windows `Program.cs`, `Help`;
both READMEs.

## Depends on
`define-interface-contracts`.
