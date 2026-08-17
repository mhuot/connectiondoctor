# Design: align-cli-verbs

## macOS verb dispatch
`Headless.run` currently checks flags in order. Replace with a small
`Verb` enum parsed from `arguments.first`, with a table mapping legacy
`--flag` → verb (printing "`--probe` is now `probe`" once on stderr). `--inspect`
is removed (retired in `docs/cli.md`); running it prints the replacement.

## Composable install
The doors are already independent in the code — the recorder is a loop, the
dashboard is an HTTP listener, MCP is a stdio server, the CLI is the binary —
so the only thing missing is letting a person choose. Four components:

| Component | Installing it means | Uninstalling it |
|---|---|---|
| `--recorder` | The collector runs at login (Run key / `SMAppService`), so history exists | Stop recording at login; recorded history is left alone — it is evidence, not configuration |
| `--dashboard` | The resident process serves the dashboard for the whole session | Stop serving; `serve` still works on demand |
| `--mcp` | Registers this binary with a detected agent (Claude Code today) as `connectiondoctor` | Removes that registration only |
| `--cli` | A `connectiondoctor` symlink on PATH (`/usr/local/bin`, or the Homebrew prefix) | Removes the symlink |

Someone genuinely wants each alone: a headless Mac mini that only records; a
laptop where the URL should always be there; an agent on a machine whose owner
does not want a resident process at all; a scripted check on a build box.

Exit status, because scripts cannot read prose:
- `0` — every requested component was installed (or was already installed).
- `1` — **nothing changed**: no requested component could be installed.
  `install --mcp` on a machine with no writable agent config exits 1, having
  printed the line to paste.
- `4` — **partial**: some requested components installed and others did not.
  `install --all` where the recorder, dashboard and CLI succeed but MCP finds
  no agent exits 4, names which succeeded and which did not, and leaves the
  successful ones installed. A script that wants all-or-nothing tests for 0; a
  script that wants "did anything happen" tests for < 4.
- `uninstall` uses the same codes for the same reasons.

No elevation, ever:
- `--cli` installs to a **writable per-user location**: `~/.local/bin` on
  macOS (creating it if needed), `%LOCALAPPDATA%\Microsoft\WindowsApps` on
  Windows — both conventionally on PATH for the user who ran the command.
  `/usr/local/bin` and a Homebrew prefix are used only when they are already
  writable without elevation.
- The command never invokes `sudo`, `runas` or an elevation prompt. When the
  chosen directory is not on the user's PATH it says so and prints the exact
  line to add, rather than silently installing something unreachable.

An agent's config is not ours to rewrite:
- `--mcp` prefers the agent's own registration command (`claude mcp add …`)
  when it is on PATH — that is the interface its owner supports.
- Failing that, the config is edited **atomically** (temp file, validated
  parse, replace) with a timestamped backup beside it, and only our own entry
  is added. Unknown keys and unrelated servers are preserved byte-for-byte.
  The command names the exact file it changed.
- `uninstall --mcp` removes **only the registration this installation
  created** — matched by server name and by the binary path pointing at this
  build — and never touches another entry.

Defaults and honesty:
- bare `install` = `--recorder --dashboard` — what it does today — and it
  prints the components it installed. `--all` adds `--mcp --cli`.
- `--mcp` writes to *someone else's file* (an agent's config). It is never
  implied by `--all` where no agent config is found, and when it cannot write
  it prints the exact line to paste rather than failing.
- Installing nothing (`install --mcp` with no agent present) is reported as
  such, not as success.
- `uninstall` with no flags removes everything this tool installed, and never
  deletes recorded history or a baseline — those outlive the installation.
- `status` reports per component: recorder (heartbeat), dashboard (the port
  answering with our `Server:` header), MCP (registration present), CLI
  (symlink present and pointing at this build), so "the dashboard is up but
  nothing is recording" is visible rather than inferred.

## Heartbeat, lock and `install` on macOS (issue #18)
macOS has no heartbeat today; Windows does (`BackgroundCollector.WriteHeartbeat`,
`ReadStatus`). macOS adopts the same contract, now written down in
`docs/cli.md` § "Resident process, heartbeat and locks":
- `Collector.tick()` writes `heartbeat.json` `{processId, startedAt,
  lastSampleAt, eventsPath}` in the data directory on every sample.
- `status`: PID alive ∧ `lastSampleAt` ≤ 3 × interval → exit 0; else exit 1
  with the failing condition; a held lock without a live heartbeat → exit 3.
- `Collector.start()` becomes `start() throws` with
  `CollectorError.storeConflict(directory:pid:)`. The app catches it and keeps
  today's read-only behaviour (`storeConflict = true`, shown in the popover);
  `collect` lets it surface: message on stderr, exit 3. One code path, two
  honest callers.
- `install`: `SMAppService.mainApp.register()` (per-user, no admin, visible in
  System Settings → Login Items), then `NSWorkspace.shared.openApplication`
  on the bundle so it is running *now*, then poll `heartbeat.json` for up to
  10 s and print the `status` line; exit 1 if none. `uninstall` =
  `unregister()`; it does not stop a running collector (same as Windows).

## `baseline` / `diff` on macOS
Baseline is a v1 envelope on disk (`baseline.json` in the data dir), same file
name Windows uses. Compare works on contract nodes: identity =
`vidPid + parent's vidPid + kind` (instance IDs differ per port on both OSes;
vidPid is the cross-platform identity per `schema-v1.md`). Windows'
`SnapshotComparer` moves to the same rule in `contract-conformance` so both
`diff`s agree on the shared fixtures.

## `ui` on both (issue #21)
Probe `GET /` on the port with a 2 s timeout. Reuse **only** if the response
carries `Server: connectiondoctor/…` (or `tbdoctor/…`) — the product identity
`docs/embedding.md` now requires on every response. Free port → start `serve`
and open. Any other 2xx → "port 8787 is held by another service; run `serve
<port>`", exit 1, nothing opened. Windows `ContractServer.IsAlreadyServing`
currently accepts any 2xx; this change fixes it and adds the header to
`ContractServer.Respond`; macOS `Serve` adds the header here too so the
resident process (`macos-resident-serves-dashboard`) inherits it.

## `collect` on macOS
The collector is inside the menu-bar app today. `collect` runs
`Collector.shared.start()` without `NSApplication` UI (an accessory-less
run loop) so a Mac mini over SSH can record with no display session. The
store lock already prevents two collectors on one machine.

## `version` (owned here; issue #19)
This change owns the `version` verb and the envelope's `producer{}` field on
both binaries. `release-pipeline` only supplies the inputs (`VERSION` env →
`CFBundleShortVersionString` + a generated `Version.swift`; `dotnet publish
-p:Version=`). Both print `<name> <version> (<sha>) · dashboard
<bundle-version>`; `--json` returns `{name, version, commit, dashboard}` — the
same object as `producer`.
