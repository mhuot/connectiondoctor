# Design: contract-conformance

## Fixture set
`docs/fixtures/<case>/{contract.v1.json, events.v1.jsonl, expected.json}`.
Cases start with the two the dashboard already ships plus a Surface recording;
each new drift bug adds a case before the fix. `expected.json` is
`{findings[], incidents[]}` in schema shape, hand-verified once, then frozen.

## The single reader
Each language gets one conformance test: read every case, run the analysis
over the envelope + events, compare to `expected.json` with a stable comparer
(sort findings by severity+title, incidents by start; compare evidence *count*
not wording, so producers may phrase evidence in their own words but must
produce the same number of facts). Wording parity is a golden-text concern
handled by `docs/cli.md` tests, not here.

## Analysis over the contract
Swift: `Diagnosis.analyze(samples:)` becomes `Diagnosis.analyze(envelope:events:)`;
`Sample` → envelope mapping already exists in `Contract.swift`; the collector
keeps `Sample` internally and converts once. C#: `SnapshotComparer.Compare`
takes two envelopes and identifies nodes by vidPid+parent; `IncidentStitcher`
takes contract events. Neither loses information the finding needs — every
field the engines read is in the envelope, or the spec gains it (additively).

## Rules → spec text
Each rule in the proposal becomes one sentence in `schema-v1.md` with a
"why" clause (the observed drift), so the next implementer sees the trap.

## Excalidraw via the shared layout
`dashboard/src/domain/excalidraw.ts` = port of `ExcalidrawExport.swift`
(deterministic seeds, palette remap, free-floating text — the design notes in
the TBDoctor README are the spec), over the TS layout engine that is already
invariant-tested. It is a pure function `toExcalidraw(envelope, style, full)`.

Three callers, one implementation:
- **Dashboard** — the Export… button calls it in the browser.
- **CLI `excalidraw` / MCP `connection_diagram`** — the collectors have no
  Node, and must not grow a copy of the layout. `scripts/build-ui` therefore
  also emits `dist/excalidraw.js`: the export compiled to one self-contained
  ES5 script exposing `toExcalidraw(json, style, full)`. Each collector embeds
  it next to the bundle and evaluates it with a JS engine it already has or
  can carry cheaply: **JavaScriptCore** on macOS (in-box framework, no
  dependency), **Jint** on Windows (pure-.NET interpreter, one small package,
  no WebView2 requirement). The function is pure and synchronous, so the
  bridge is: read envelope → call → write file.

Consequences: `Diagram.swift` and `ExcalidrawExport.swift` are deleted; the
Windows `excalidraw` verb and `connection_diagram` stop being placeholders;
the same fixture exports byte-identically on both. If the JS-engine bridge
ever proves brittle, the fallback is to keep it dashboard-only and have the
CLI print the URL — recorded so that choice is made with data, not by adding a
fourth copy of the layout.
