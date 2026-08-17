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
- **`--json`** on any verb that reports state emits one of the Contract v1
  **documents** ([`schema-v1.md` § Documents](schema-v1.md#documents)): the
  envelope (`probe`, `tree`, `contract`), a report (`report`), a diff (`diff`)
  — never an ad-hoc JSON.
- **`--redact`** on `contract`, `report` and `diff` (and always in `bundle`)
  applies the share-scoped redaction in `schema-v1.md` § Redaction:
  pseudonymous `host.id`, `host.name` and node ids with every reference
  rewritten; user-assigned names replaced by conservative labels; no
  `platform{}`, `unitKey` or serials — recursively, including embedded
  snapshots.
- **stdout is the answer, stderr is commentary.** Progress, deprecation notices
  and errors go to stderr, so `--json` output pipes cleanly.
- **Exit codes:** `0` ok · `1` usage or runtime error, or an `install`/`uninstall`
  where **none** of the requested components ended up in the requested state ·
  `2` a `critical` finding was reported (`report`, `diff`) · `3` store-lock
  conflict — another collector owns the data directory (`collect`, `status`) ·
  `4` **partial** install/uninstall: some requested components reached the
  requested state and others did not. For `install`/`uninstall` the code
  answers *is the desired state satisfied* — not *did anything change*: a
  component that was already installed exits `0` like one just installed. The
  per-component line says which (`installed` / `already installed` /
  `not installed: <reason>`) for scripts that need to tell them apart. The
  codes are a set, not a scale — do not compare them with `<` or `>`.
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
| `report [--hours N] [--json]` | Ranked findings with evidence, then incidents newest-first, from recorded history (default 6 h) | text; `--json` = **report** document | ✓ both (Windows: sustained deficit + grouped loss + baseline diff; link drops not observable yet) |
| `baseline save [path]` | Save the current state as the known-good reference | writes a v1 envelope | win ✓; **mac to add** |
| `diff [baseline] [--json]` | Compare now against the known-good: findings, missing, added | text; `--json` = **diff** document | win ✓; **mac to add** |
| `watch` | Live one-line-per-sample table until interrupted | text | ✓ |
| `collect` | Run the recorder in the foreground with no UI (SSH, services); refuses with exit 3 if another collector holds the store lock | log lines | win ✓ (`collect`/`watch` alias); **mac to add** (headless collector) |
| `serve [port] [--bind lan]` | HTTP `/contract`, `/events` and the dashboard bundle | — | ✓ (mac `--serve`) |
| `ui [port]` | Open the dashboard in the default browser; reuses a server on the port only if it answers with our `Server:` header, starts `serve` if the port is free, and refuses (exit 1) if something else holds it | — | win ✓; **mac to add** |
| `mcp` | MCP server on stdio ([`mcp.md`](mcp.md)) | JSON-RPC | ✓ (mac `--mcp` with `tb_*` names until `align-cli-verbs`; win `mcp`) |
| `install` | Register the resident process (menu bar / tray + recorder) to start at login, start it now, and wait for its first heartbeat | text; exit 1 if no heartbeat within 10 s | win ✓; **mac to add** (`SMAppService`) |
| `uninstall` | Remove the login registration; stop nothing that is running | text | win ✓; **mac to add** |
| `status` | Is the recorder running, when was the last sample, where the data is | text; `0` healthy · `1` not running / stale / no heartbeat · `3` lock conflict | win ✓; **mac to add** |
| `excalidraw <out.excalidraw> [--style cascade\|topDown\|flow] [--full]` | Topology as an Excalidraw document | file | mac ✓; **win to add** |
| `bundle <out.zip> [--hours N] [--scope token]` | Redacted support bundle: envelope, report, events window and a manifest of what was transformed, under one share scope (`schema-v1.md` § Redaction); the token is generated and managed unless `--scope` is given (warns on reuse) | zip | **both to add** (`contract-conformance`) |
| `version` | Version, commit, and the embedded dashboard bundle's version | text; `--json` | **both to add** |
| `help` | This table | text | ✓ |

Retired: macOS `--inspect <file>` (opened a native window on a recording) — the
replacement is dropping the file on the dashboard, which works today. Windows
`snapshot [path]` becomes an alias of `contract [path]` and its native JSON
format is retired in favour of the envelope (`baseline` follows; see
`contract-conformance`).

## Resident process, heartbeat and locks

The rules `install`, `status` and `collect` share on both platforms (Windows
implements them today in `BackgroundCollector`; macOS adopts them in
`align-cli-verbs`):

- **Heartbeat.** Every collector process — menu bar / tray, or a foreground
  `collect` — writes `heartbeat.json` in the data directory on every sample:
  `{ "processId", "startedAt", "lastSampleAt", "eventsPath" }`.
- **Healthy** means the heartbeat's process is alive **and** `lastSampleAt`
  is no older than 3 × the sample interval (15 s at the 5 s cadence). `status`
  prints the PID and the age of the last sample and exits `0`; otherwise it
  says which condition failed and exits `1`.
- **One collector per data directory.** The collector holds an exclusive lock
  (`.collector.lock`, `flock` / `FileShare.None`) for its lifetime. A second
  collector — `collect` when the resident process is already recording, or a
  second copy of the app — **refuses**: it prints "another collector owns
  <dir> (PID n)" on stderr and exits `3`. The resident process, on finding the
  lock held, stays up read-only and shows the conflict in its menu; it does
  not silently record.
- **`install` starts now.** Registering at login is not enough to make "the
  collector is running" true. `install` registers (Run key / `SMAppService`),
  launches the resident process, then waits up to 10 s for a fresh heartbeat
  and reports the same line `status` would; exit `1` if none appears.
- **Port reuse needs identity.** `ui` and "Open dashboard…" reuse a server on
  the port only if `GET /` answers with `Server: connectiondoctor/…` (see
  [`embedding.md`](embedding.md)). Any other service on 8787 is a conflict,
  reported plainly, never opened.

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
