# Tasks: add-react-dashboard

## 1. Foundation
- [x] 1.1 Scaffold Vite + React 18 + TypeScript app with vitest; strict TS; no default exports
- [x] 1.2 `src/contract/types.ts` — Connection Contract v1 model (envelope, power, node, display, event, finding, incident)
- [x] 1.3 `src/contract/parse.ts` — validate + normalize per contract-ingest spec (schema check, orphan-to-root, JSONL with skip counting, fullSnapshot sync points)
- [x] 1.4 Fixtures from real fleet recordings (mini KVM flip, Surface-dock chain, 08-13 deficit incident) + parser tests over them

## 2. Domain
- [x] 2.1 `domain/topology.ts` — tree build + physical collapse (port of TBDoctor Topology: enclosure fold, controller-silicon list, +N internal, never fold thunderbolt nodes)
- [x] 2.2 `domain/layout.ts` — cascade/topDown/flow placement + orthogonal edge routing incl. out-of-footprint DP edges (port of TBDoctor Diagram); invariant tests (overlap/orthogonality/canvas); golden-vs-Swift deferred — needs a dump mode in TBDoctor
- [x] 2.3 `domain/incidents.ts` — event grouping (30s gap), root attribution, sharedParent
- [x] 2.4 `domain/migrate.ts` — cross-host migration detection per fleet-view spec (120s window, branch collapse, count-matching for duplicate vidPids) + tests

## 3. Views
- [x] 3.1 TopologyView: SVG canvas, protocol colours, tunneled dashes, zoom, layout picker, physical/logical toggle (persisted)
- [x] 3.2 Inspector panel: all node fields, copy per row, VID:PID lookup
- [x] 3.3 TimelineView: step-interpolated link chart, power chart, device count, root-event rules, findings with evidence, incident list
- [x] 3.4 FleetView: host cards + migration arrows; "recorded <time>" labelling throughout

## 4. Data sources
- [x] 4.1 `data/sources.ts` — Source interface; FileSource (drag-drop + picker)
- [x] 4.2 HttpSource stub behind the same interface (loadHttp in sources.ts; no UI affordance yet — needs collectors emitting v1 first)

## 5. Wrap-up
- [x] 5.1 README with screenshots from fixtures; note Tauri wrap + collector endpoints as follow-up changes
- [ ] 5.2 `openspec archive add-react-dashboard` after review
