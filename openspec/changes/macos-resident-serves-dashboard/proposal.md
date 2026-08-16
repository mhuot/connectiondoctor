# Proposal: macos-resident-serves-dashboard

## Why
On Windows the resident process (tray) serves the dashboard for the whole
login session and the native views are gone. On macOS the menu-bar app records
but does not serve — `--serve` is a separate headless process that ends in
`dispatchMain()` — and it still opens three native SwiftUI windows
(Connections, Timeline, Inspector; ~790 LOC) that duplicate the dashboard. The
"open one URL" story is therefore only true on Windows, and the macOS UI is a
second implementation that will drift.

## What
- Menu-bar app starts the HTTP listener in-process at launch (loopback, port
  8787; `--bind lan` via a preference), without taking over the main queue.
  If another instance holds the port, it just opens that one.
- Menu-bar buttons "Open dashboard…" (replaces "Open timeline…" and
  "Connections…") open `http://localhost:8787`; app reopen does the same.
- Retire `ConnectionsWindow.swift`, `DiagramView.swift`, `InspectorPanel.swift`,
  `TimelineWindow.swift`, `TimelineView.swift`, `Inspect.swift`.
- Keep `Diagram.swift` (layout) and `ExcalidrawExport.swift` — used by the
  `excalidraw` verb and `connection_diagram` — until `contract-conformance`
  moves the export to the shared TS layout.
- The menu-bar popover keeps its status rows and the leading root cause (it is
  the status light), reading from the same analysis that feeds the envelope.
- `serve` verb remains for headless use; `ui` opens the browser.

## Non-goals
Auth on the LAN binding; a native window that hosts a WKWebView (a browser tab
is fine and matches Windows); moving Excalidraw export.

## Impact
Capability `resident-process` (new); macOS `App.swift`, `Serve.swift`
(listener as an object with start/stop), `MenuBarView.swift`; six files
deleted; README screenshots of native windows replaced by dashboard ones.

## Depends on
`contract-findings-incidents` — the dashboard must show findings before the
native Timeline that shows them is deleted.
