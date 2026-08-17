# Tasks: contract-conformance
- [ ] 1.1 `docs/fixtures/` with three cases and `expected.json`; README in the folder
- [ ] 1.1b `docs/schema/v1/{envelope,report,diff}.schema.json` (JSON Schema 2020-12) for the three documents incl. Finding/Incident/Node; dashboard parser tests and every conformance test validate against them — issue #20
- [ ] 1.2 `docs/schema-v1.md`: the nine rules with why-clauses; `acChanged`; `source` on link events; arch enum; `tb.route`
- [ ] 1.3 macOS analysis over envelope+events; conformance test
- [ ] 1.4 Windows analysis over envelope+events (vidPid+parent identity — removes the interim `note` from `connection_diff`/`diff`, issue #19; `adapter` always present, displays[], tunneled rule, linkDown/linkUp poll-derived, trim→snapshot); conformance test
- [ ] 1.5 Dashboard: read display nodes with `attachedTo`; conformance test over `domain/`
- [ ] 1.6 `domain/excalidraw.ts` + `build-ui` emits `excalidraw.js`; collectors embed and run it (JavaScriptCore / Jint) for `excalidraw` and `connection_diagram`; delete `Diagram.swift`, `ExcalidrawExport.swift`
- [ ] 1.7 Regenerate all shipped fixtures from current producers; delete stale `surface-chain.v1.json`
