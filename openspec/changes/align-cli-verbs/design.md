# Design: align-cli-verbs

## macOS verb dispatch
`Headless.run` currently checks flags in order. Replace with a small
`Verb` enum parsed from `arguments.first`, with a table mapping legacy
`--flag` → verb (printing "`--probe` is now `probe`" once on stderr). `--inspect`
is removed (retired in `docs/cli.md`); running it prints the replacement.

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
