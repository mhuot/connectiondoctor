# Command-line interface

> **Status: proposed** — introduced by `openspec/changes/define-interface-contracts`
> and implemented by `align-cli-verbs`. Until that change lands, each binary's
> `help` output is the truth; this page is what both are converging on.

One verb set, two binaries: `tbdoctor` (macOS, `TBDoctor.app/Contents/MacOS/TBDoctor`)
and `connectiondoctor` (Windows, `ConnectionDoctor.exe`). Every verb behaves
the same on both, prints the same shape, and exits with the same codes. Where a
platform genuinely cannot do something it says so on stderr and exits 1; it
does not silently omit the verb.

## Conventions

- **Verbs, not flags.** `tbdoctor probe`, not `tbdoctor --probe`. macOS keeps
  the `--verb` forms as deprecated aliases for one release, printing a one-line
  notice on stderr.
- **`--json`** on any verb that reports state emits Connection Contract v1
  shapes (see [`schema-v1.md`](schema-v1.md)) — never an ad-hoc JSON.
- **stdout is the answer, stderr is commentary.** Progress, deprecation notices
  and errors go to stderr, so `--json` output pipes cleanly.
- **Exit codes:** `0` ok · `1` usage or runtime error · `2` a `critical`
  finding was reported (`report`, `diff`). Scripts can act on the code alone.
- **Data directory:** `~/Library/Application Support/TBDoctor` (macOS),
  `%LOCALAPPDATA%\ConnectionDoctor` (Windows). Override with
  `CONNECTIONDOCTOR_DIR`; macOS also honours the older `TBDOCTOR_DIR`.
- **Default port** 8787, loopback. `--bind lan` is opt-in and unauthenticated.

## Verbs

| Verb | Does | Output | Both today? |
|---|---|---|---|
| `probe [--json]` | Current state, once: power, Thunderbolt/USB4, USB tree with negotiated speeds and VID:PID | text table; `--json` = v1 envelope | ✓ (mac `--probe`) |
| `tree [--full] [--json]` | Topology as an indented tree; `--full` includes internal hubs and controller silicon | text; `--json` = envelope | ✓ (mac `--tree`) |
| `contract [path]` | The v1 envelope, to stdout or a file | JSON | ✓ (mac `--contract`) |
| `report [--hours N] [--json]` | Ranked findings with evidence, then incidents newest-first, from recorded history (default 6 h) | text; `--json` = `{findings[], incidents[]}` per schema | mac ✓ (`--report`); win: incidents only, no ranked findings — closed by `contract-findings-incidents` |
| `baseline save [path]` | Save the current state as the known-good reference | writes a v1 envelope | win ✓; **mac to add** |
| `diff [baseline] [--json]` | Compare now against the known-good: findings, missing, added | text; `--json` = `{findings[], missing[], added[]}` | win ✓; **mac to add** |
| `watch` | Live one-line-per-sample table until interrupted | text | ✓ |
| `collect` | Run the recorder in the foreground with no UI (SSH, services) | log lines | win ✓ (`collect`/`watch` alias); **mac to add** (headless collector) |
| `serve [port] [--bind lan]` | HTTP `/contract`, `/events` and the dashboard bundle | — | ✓ (mac `--serve`) |
| `ui` | Open the dashboard in the default browser, starting `serve` if nothing holds the port | — | win ✓; **mac to add** |
| `mcp` | MCP server on stdio ([`mcp.md`](mcp.md)) | JSON-RPC | mac ✓ (`--mcp`); **win to add** (`add-windows-mcp`) |
| `install` | Register the resident process (menu bar / tray + recorder) to start at login, and start it now | text | win ✓; **mac to add** (`SMAppService`) |
| `uninstall` | Remove the login registration; stop nothing that is running | text | win ✓; **mac to add** |
| `status` | Is the recorder running, when was the last sample, where the data is | text; exit 1 if not running | win ✓; **mac to add** |
| `excalidraw <out.excalidraw> [--style cascade\|topDown\|flow] [--full]` | Topology as an Excalidraw document | file | mac ✓; **win to add** |
| `version` | Version, commit, and the embedded dashboard bundle's version | text; `--json` | **both to add** |
| `help` | This table | text | ✓ |

Retired: macOS `--inspect <file>` (opened a native window on a recording) — the
replacement is dropping the file on the dashboard, which works today. Windows
`snapshot [path]` becomes an alias of `contract [path]` and its native JSON
format is retired in favour of the envelope (`baseline` follows; see
`contract-conformance`).

## Text output shapes

Human output is specified loosely — the point is that the *same* facts appear in
the *same* order on both platforms, so a person who learnt one reads the other:

- `probe`: **Power** block (source, adapter identity/watts, battery %, rate,
  external-connected) → **Thunderbolt / USB4** block (device, link Gb/s, depth)
  → **USB** table (`kind  speed  name  VID:PID`, tree-indented). Anything the
  platform cannot know prints `unknown`, never a guess.
- `report`: findings first, ranked `critical` → `warning` → `info`, each as
  `SEVERITY: title` / explanation / evidence lines / `Action:` recommendation /
  `confidence:`; then incidents newest-first as `start  duration  root-event
  devices-lost`.
- `diff`: findings (same shape as report), then `Missing (n)` and `Added (n)`
  lists as `kind  name  [VID:PID]`.

Golden-output tests hold both binaries to these shapes against the shared
fixtures in `docs/fixtures/` (see `contract-conformance`).
