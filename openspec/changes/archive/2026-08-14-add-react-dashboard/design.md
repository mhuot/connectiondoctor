# Design: add-react-dashboard

## Shell decision: web React now, Tauri later, React Native never

| Option | Verdict | Reasoning |
|---|---|---|
| React Native (`react-native-macos` + `react-native-windows`) | **Rejected** | Both are platform forks that lag core RN by versions. Menu-bar/tray chrome — where these tools live — is not first-class on either, so the hard 40% (NSStatusItem popover, NotifyIcon, launchd/Task Scheduler) becomes custom native modules while RN shares only the easy 60%. The diagram canvas would still need a per-platform SVG stack. |
| Electron | Rejected | Works, but ~150MB per install for a diagnostic sidecar is disproportionate; two of the four fleet machines are ARM laptops where footprint matters. |
| **Tauri v2 shell over web React** | **Chosen (later change)** | System webview (~10MB), first-class tray on macOS + Windows incl. ARM64, sidecar model fits the existing native collectors exactly. |
| Web-first React (Vite) | **Chosen (this change)** | Everything Tauri would wrap. Runs in any browser today against fixture files or a collector's HTTP endpoint; zero shell assumptions means the Tauri change is packaging, not rework. |

The native SwiftUI/WPF apps keep their menu-bar/tray roles and gain an "Open
dashboard" action later. The dashboard is the deep-dive and fleet surface.

## Architecture

```
src/
  contract/        types.ts (v1 model) · parse.ts (validate + normalize) · fixtures/
  domain/          topology.ts (tree build, physical collapse — port of
                   TBDoctor Topology) · layout.ts (cascade/topDown/flow —
                   port of TBDoctor Diagram) · migrate.ts (cross-host
                   device-migration detection) · incidents.ts
  components/      TopologyView · TimelineView · FleetView · Inspector ·
                   Legend · shared chips/cards
  data/            sources.ts (FileSource, HttpSource) · store.ts
```

Rules carried over from the native implementations (each encodes a real
mistake made during the original investigation):

- **Pure domain, thin components.** Layout and topology logic are plain TS
  functions over contract types, unit-tested without DOM. The SwiftUI engine's
  layout math ports nearly 1:1 (it is already geometry over structs).
- **Evidence is mandatory.** Findings render title + evidence list; no
  verdict-only display.
- **Tunneled means tunneled.** Dashed links only for DP/USB3 behind a
  Thunderbolt node; USB 2.0 never dashes.
- **Boxes size to their text.** Truncating "4-Port USB 2.0 Hub — LG
  Electronics Inc." discards the identifying word. Measure, don't clamp.
- **Root vs fallout.** Only root events get timeline rule-marks; grouped
  losses attribute to `sharedParent`.
- **Recorded ≠ stale.** Fixture/recorded data is labelled "recorded <time>",
  never shown with a live-age indicator.

## Migration detection (fleet view)

Given per-host event streams, a `deviceRemoved(vidPid)` on host A followed
within `T = 120s` by `deviceAdded(same vidPid)` on host B is a **migration**
(A → B). Multiple devices migrating in one window with a shared parent on both
sides collapse into a single "branch moved" migration (the KVM case: LG hub +
keyboard + mouse + monitor controls as one arrow, not four). Ambiguity rule:
identical vidPids present on multiple hosts (two MX Verticals) — migrations
require the *remove* precede the *add* and match counts; otherwise render as
independent add/remove.

## Data sources

- `FileSource`: drag-drop or file-pick contract JSON / events JSONL. Primary
  path until collectors emit v1.
- `HttpSource`: polls `GET /contract` on a collector base URL (future; behind
  the same `Source` interface so components never know the difference).
- Fixtures: real recordings from the fleet (mini KVM flip, Surface-dock chain,
  the 08-13 power-deficit incident) converted to v1 — they double as parser
  test vectors.

## Risks

- Contract emission (tbdoctor#1 / connectiondoctor#15) not landed → dashboard
  ships against fixtures; ingest is contract-first so no rework when live
  sources appear.
- Layout port fidelity → golden tests: same fixture in, assert node frames
  match the Swift engine's captured output within tolerance.
