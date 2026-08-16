# Design: align-cli-verbs

## macOS verb dispatch
`Headless.run` currently checks flags in order. Replace with a small
`Verb` enum parsed from `arguments.first`, with a table mapping legacy
`--flag` → verb (printing "`--probe` is now `probe`" once on stderr). `--inspect`
is removed (retired in `docs/cli.md`); running it prints the replacement.

## `install` on macOS
`SMAppService.mainApp.register()` registers the running `.app` as a login item
(per-user, no admin, shows in System Settings → Login Items). `uninstall` =
`unregister()`. `status` reads the heartbeat the collector already writes.
This closes the README's "add it to Login Items by hand".

## `baseline` / `diff` on macOS
Baseline is a v1 envelope on disk (`baseline.json` in the data dir), same file
name Windows uses. Compare works on contract nodes: identity =
`vidPid + parent's vidPid + kind` (instance IDs differ per port on both OSes;
vidPid is the cross-platform identity per `schema-v1.md`). Windows'
`SnapshotComparer` moves to the same rule in `contract-conformance` so both
`diff`s agree on the shared fixtures.

## `ui` on both
Probe `GET /` on the port with a 2 s timeout; if 200, open the browser; else
start `serve` and open. Windows already does this (`ContractServer.OpenDashboard`).

## `collect` on macOS
The collector is inside the menu-bar app today. `collect` runs
`Collector.shared.start()` without `NSApplication` UI (an accessory-less
run loop) so a Mac mini over SSH can record with no display session. The
store lock already prevents two collectors on one machine.

## `version`
Injected at build: `build_app.sh VERSION=…` writes `CFBundleShortVersionString`
and a `Version.swift` constant; `dotnet publish -p:Version=`. Both print
`<name> <version> (<sha>) · dashboard <bundle-version>`; `--json` returns
`{name, version, commit, dashboard}` — the same object as `producer` in the
envelope (`release-pipeline`).
