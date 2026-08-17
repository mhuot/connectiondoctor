# Design: macos-resident-serves-dashboard

## Listener lifecycle
`Serve.run` becomes `Server(port:lan:)` with `start()`/`stop()` on a private
queue; `--serve`/`serve` wraps it in `dispatchMain()` as today; the app calls
`start()` from `applicationDidFinishLaunching` after `Collector.shared.start()`.
Port conflict → log once, keep running (the collector still matters). The
"Open dashboard…" button probes `GET /` and opens it **only if the response
carries our `Server: tbdoctor/…` / `connectiondoctor/…` header** (issue #21;
`docs/embedding.md`); an unrelated service on the port is reported in the
popover ("port 8787 is held by another app — run `tbdoctor serve <port>`"),
never opened. Same rule as `ui` in `align-cli-verbs`, which is where the
header is added to `Serve`.

## What the popover keeps
Status light, current link/adapter/battery/device rows, leading root cause,
last incident, and two buttons: "Open dashboard…" and "Quit". These read the
same `Collector` state; nothing here draws a topology or a chart.

## What is deleted, and what replaces it
| Native | Replacement |
|---|---|
| Connections window (3 layouts, physical/logical, inspector) | dashboard Topology view — already a pure-TS port of the same engine, with the same three layouts and inspector |
| Timeline window (charts + findings + incidents) | dashboard Timeline + Findings panel (`contract-findings-incidents`) |
| `--inspect samples.jsonl` | drop the file on the dashboard |
Deletion is one commit after `contract-findings-incidents` ships, so `git log`
shows the handover.

## Login item
`SMAppService` registration (from `align-cli-verbs`) makes "installed once, URL
always there" true on macOS the way `tray` at login makes it true on Windows.
