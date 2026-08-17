# ConnectionDoctor

ConnectionDoctor is a Windows-native diagnostic tool for USB-C, USB4, docks, displays, hubs, power, and downstream peripherals. It is the Windows counterpart to [TBDoctor](../macos/README.md), but the broader name is intentional: failures often cross protocol boundaries and are not necessarily Thunderbolt failures.

The first supported case came from a Surface Laptop 7 connected to an LG UltraWide with an integrated KVM. Video continued working while the monitor's USB hub and its keyboard and mouse failed to enumerate. Cold power-cycling the monitor restored the full branch.

## Current MVP

- Enumerates only devices that are physically present now; historical Device Manager ghosts are excluded.
- Reads the Windows parent-device graph through SetupAPI and CfgMgr32.
- Reports the speed each USB port actually negotiated, asked of the hub itself.
- Shows USB, USB4, Thunderbolt, monitor, HID, keyboard, mouse, and firmware nodes.
- Saves auditable JSON snapshots.
- Captures a known-good baseline and compares it with the current setup.
- Detects the initial high-value signature: an LG display remains active while its expected USB hub branch is missing.
- Continuously records connection changes to bounded JSONL, with hourly full snapshots as sync points.
- Registers itself per-user to start collecting at login.
- Hides built-in laptop devices in the dashboard by default, with an **Include built-in devices** toggle.
- Emits **Connection Contract v1**, the schema shared with TBDoctor, either as a
  one-shot export or over HTTP for the [Connection Dashboard](../dashboard/README.md).
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
| `tray` | Notification-area status; serves the dashboard for the session |
| `mcp` | MCP server on stdio for coding agents (see below) |

The default baseline is stored under `%LOCALAPPDATA%\ConnectionDoctor\baseline.json`.
Continuous events are stored under `%LOCALAPPDATA%\ConnectionDoctor\events.jsonl` and trimmed at 24 MB.

## Use from a coding agent

ConnectionDoctor is an MCP server, the same tool set TBDoctor serves on macOS
([`docs/mcp.md`](../docs/mcp.md)), so Claude Code, Copilot in VS Code and Cursor
can query the hardware directly instead of being handed pasted terminal output:

```powershell
claude mcp add connectiondoctor -- C:\path\to\ConnectionDoctor.exe mcp
```

Tools: `connection_probe` (current state as a Connection Contract v1 envelope),
`connection_diagnose` (findings with evidence), `connection_incidents`
(recorded fault incidents), `connection_diff` (what is missing versus the saved
baseline), `connection_diagram` (interim: points at the dashboard's export until
the shared Excalidraw export lands). Every result is a contract document, so an
agent's instructions written against a Mac work here unchanged.

## The dashboard

The [Connection Dashboard](../dashboard/README.md) is
compiled into the exe. There is nothing to install alongside it — no Node, no
second app, no separate web server:

```powershell
connectiondoctor install          # start recording, and keep recording at login
connectiondoctor ui               # opens the dashboard in your browser
```

`ui` serves on 127.0.0.1:8787 and opens it; if `tray` or `serve` already holds
the port it just opens the browser. The page connects to the machine it is
served from, so the topology is on screen with nothing to type.

`install` registers `tray` at login, and the tray serves the dashboard for the
whole session — so after installing once, the URL is simply always there. The
tray itself is a status light and a launcher: it shows whether the collector is
recording, and copies a paste-into-a-ticket summary. There is no second set of
views to drift out of step with the React ones.

Add `--bind lan` to `serve` to view the fleet from another machine. That is
unauthenticated read-only telemetry, so it is opt-in and needs a one-time
`netsh http add urlacl` from an elevated prompt.

Rebuilding the embedded UI needs Node, and only for whoever builds a release:

```powershell
..\scripts\build-ui.ps1 -Target windows   # builds ..\dashboard, stages dist here
dotnet build .\ConnectionDoctor.sln
```

A build with nothing staged still works; `/` then explains that instead of
serving a UI.

Link speeds are measured, not inferred. SetupAPI exposes no speed property, so
every hub is asked directly — `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX`
per port, plus the V2 form to separate SuperSpeed from SuperSpeed+ — and each
device is matched to its own port through `SPDRP_ADDRESS`. A port that will not
answer stays `unknown`, because a wrong speed is worse than an absent one.

That makes a degraded link visible rather than merely suspected: a hub whose
descriptor says USB 2.0 while its port negotiated 12 Mb/s is a cable or
connector problem, and everything behind it inherits the ceiling.

Two things remain deliberately honest about what Windows does not tell us:

- `tunneled` is always false. Knowing a link runs at 10 Gb/s does not establish
  that USB4 is tunneling it; only USB4 router facts would.
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
6. ~~MCP tools for probe, diagnosis, incidents, and diagrams.~~ Done: `mcp` verb, see "Use from a coding agent".
7. USB4 router facts, so `tunneled` stops being a flat false.

## Naming

**ConnectionDoctor** is preferred over `TBDoctor-Windows` or `USBDoctor`: it describes the user problem rather than prematurely blaming one protocol. The same visible failure may originate in USB, USB4, DisplayPort, a monitor hub, power delivery, firmware, or sleep-state transitions.
