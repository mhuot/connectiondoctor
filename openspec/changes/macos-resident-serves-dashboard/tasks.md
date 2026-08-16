# Tasks: macos-resident-serves-dashboard
- [ ] 1.1 `Server` object with start/stop; app starts it at launch; `serve` verb unchanged for headless
- [ ] 1.2 Menu bar: "Open dashboard…" (probe-then-open), reopen-app opens the dashboard; popover keeps status + root cause
- [ ] 1.3 Delete ConnectionsWindow, DiagramView, InspectorPanel, TimelineWindow, TimelineView, Inspect; keep Diagram + ExcalidrawExport
- [ ] 1.4 README: dashboard screenshots replace native ones; "Menu bar" section rewritten
- [ ] 1.5 Verify: fresh login → menu bar icon → dashboard answers at :8787 with no terminal
