# ConnectionDoctor

ConnectionDoctor is a Windows-native diagnostic tool for USB-C, USB4, docks, displays, hubs, power, and downstream peripherals. It is the Windows counterpart to [TBDoctor](https://github.com/mhuot/tbdoctor), but the broader name is intentional: failures often cross protocol boundaries and are not necessarily Thunderbolt failures.

The first supported case came from a Surface Laptop 7 connected to an LG UltraWide with an integrated KVM. Video continued working while the monitor's USB hub and its keyboard and mouse failed to enumerate. Cold power-cycling the monitor restored the full branch.

## Current MVP

- Enumerates only devices that are physically present now; historical Device Manager ghosts are excluded.
- Reads the Windows parent-device graph through SetupAPI and CfgMgr32.
- Shows USB, USB4, Thunderbolt, monitor, HID, keyboard, mouse, and firmware nodes.
- Saves auditable JSON snapshots.
- Captures a known-good baseline and compares it with the current setup.
- Detects the initial high-value signature: an LG display remains active while its expected USB hub branch is missing.
- Continuously records connection changes to bounded JSONL, with hourly full snapshots as sync points.
- Registers itself per-user to start collecting at login.
- Hides built-in laptop devices in the dashboard by default, with an **Include built-in devices** toggle.
- Emits **Connection Contract v1**, the schema shared with TBDoctor, either as a
  one-shot export or over HTTP for the [Connection Dashboard](https://github.com/mhuot/connection-dashboard).
- Runs natively on Windows ARM64 and x64 with .NET 8.

## Build and run

```powershell
dotnet build .\ConnectionDoctor.sln
dotnet run --project .\src\ConnectionDoctor -- probe
dotnet run --project .\src\ConnectionDoctor -- tree
dotnet run --project .\src\ConnectionDoctor -- baseline save
dotnet run --project .\src\ConnectionDoctor -- diff
```

Once a known-good baseline is saved, `diff` answers the most useful question: what connection branch is missing now that was present when the desk worked?

Publish and install continuous collection:

```powershell
dotnet publish .\src\ConnectionDoctor -c Release -r win-arm64 --self-contained false -o .\artifacts\win-arm64
.\artifacts\win-arm64\ConnectionDoctor.exe install
.\artifacts\win-arm64\ConnectionDoctor.exe status
.\artifacts\win-arm64\ConnectionDoctor.exe ui
```

## Commands

| Command | Purpose |
|---|---|
| `probe` | Print the current present-only connection state |
| `tree` | Print the parent-device topology |
| `snapshot [path]` | Save the current state as JSON |
| `contract [path]` | Export the current state as a Connection Contract v1 envelope |
| `serve [port]` | Serve `/contract` and `/events` for the dashboard (default 8787, loopback) |
| `baseline save [path]` | Save a known-good state |
| `diff [path]` | Compare current state with known-good and diagnose changes |
| `report` | Stitch recorded changes into incidents and print them newest-first |
| `collect` | Record a present-device snapshot every five seconds |
| `watch` | Alias for `collect`; print each connection change live |
| `status` | Report collector process and heartbeat health |
| `install` | Start collecting and register startup for the current user |
| `uninstall` | Remove the startup registration |
| `ui` | Serve and open the Connection Dashboard in a browser |
| `winui` | Open the legacy WinForms dashboard window |
| `tray` | Run the notification-area dashboard host |

The default baseline is stored under `%LOCALAPPDATA%\ConnectionDoctor\baseline.json`.
Continuous events are stored under `%LOCALAPPDATA%\ConnectionDoctor\events.jsonl` and trimmed at 24 MB.

## The dashboard

The [Connection Dashboard](https://github.com/mhuot/connection-dashboard) is
compiled into the exe. There is nothing to install alongside it — no Node, no
second app, no separate web server:

```powershell
connectiondoctor install          # start recording, and keep recording at login
connectiondoctor ui               # opens the dashboard in your browser
```

`ui` serves on 127.0.0.1:8787 and opens it; if a collector or `serve` already
holds the port it just opens the browser. The page connects to the machine it
is served from, so the topology is on screen with nothing to type.

Add `--bind lan` to `serve` to view the fleet from another machine. That is
unauthenticated read-only telemetry, so it is opt-in and needs a one-time
`netsh http add urlacl` from an elevated prompt.

Rebuilding the embedded UI needs Node, and only for whoever builds a release:

```powershell
.\scripts\build-ui.ps1           # builds ../connection-dashboard, stages dist
dotnet build .\ConnectionDoctor.sln
```

A build with nothing staged still works; `/` then explains that instead of
serving a UI.

Two fields are deliberately honest about what Windows does not tell us here:

- `nodes[].protocol` is `unknown` for USB links, and `tunneled` is always false.
  SetupAPI reports no negotiated link speed, so guessing usb2 against usb3 —
  or claiming a USB4 tunnel — would be inventing evidence. Real speeds need
  `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX`.
- `displaysKnown` is false and `displays` is absent, because native pixel sizes
  need `QueryDisplayConfig`. "We do not know" is a different claim from
  "nothing is attached".

## Why not TBDoctor for Windows?

TBDoctor's architecture transfers well, but its name and some diagnoses are specific to macOS Thunderbolt telemetry. ConnectionDoctor covers failures where:

- DisplayPort video works but a monitor's USB hub is wedged.
- A USB4 router survives while one downstream USB branch disappears.
- Several devices vanish together behind a shared parent.
- USB-C power delivery or Modern Standby resets a connection.
- A host switch produces a topology different from the known-good state.

## Next milestones

1. Continuous bounded JSONL recording with 5-second samples.
2. ETW ingestion from USBHUB3, USBXHCI, USB-UCX, UCSI, USB4 router, Kernel-PnP, and Kernel-Power providers.
3. Incident stitching that separates root events from downstream fallout.
4. Modern Standby and lid-action awareness.
5. QueryDisplayConfig display-path correlation and USB4 route details.
6. Retire the WinForms dashboard once the React UI covers the tray workflows.
7. MCP tools for probe, diagnosis, incidents, and diagrams.
8. Real USB link speeds and USB4 tunnel facts, so `protocol` and `tunneled` stop saying "unknown".

## Naming

**ConnectionDoctor** is preferred over `TBDoctor-Windows` or `USBDoctor`: it describes the user problem rather than prematurely blaming one protocol. The same visible failure may originate in USB, USB4, DisplayPort, a monitor hub, power delivery, firmware, or sleep-state transitions.
