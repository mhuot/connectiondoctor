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

## Commands

| Command | Purpose |
|---|---|
| `probe` | Print the current present-only connection state |
| `tree` | Print the parent-device topology |
| `snapshot [path]` | Save the current state as JSON |
| `baseline save [path]` | Save a known-good state |
| `diff [path]` | Compare current state with known-good and diagnose changes |
| `report [path]` | Alias for `diff` |

The default baseline is stored under `%LOCALAPPDATA%\ConnectionDoctor\baseline.json`.

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
6. Timeline and physical/logical topology UI inspired by TBDoctor.
7. MCP tools for probe, diagnosis, incidents, and diagrams.

## Naming

**ConnectionDoctor** is preferred over `TBDoctor-Windows` or `USBDoctor`: it describes the user problem rather than prematurely blaming one protocol. The same visible failure may originate in USB, USB4, DisplayPort, a monitor hub, power delivery, firmware, or sleep-state transitions.
