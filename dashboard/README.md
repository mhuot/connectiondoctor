# Connection Dashboard

One React UI for the connection-diagnostics family — [TBDoctor](../macos/README.md)
(macOS) and [ConnectionDoctor](../windows/README.md) (Windows) — reading the
shared **Connection Contract v1** (`../docs/schema-v1.md`).

Spec-driven with [OpenSpec](https://github.com/Fission-AI/OpenSpec): see
`openspec/changes/add-react-dashboard/` for the proposal, design (including the
recorded decision to go web-first React + Tauri later, with React Native
evaluated and rejected), capability specs, and task list.

<img src="../docs/images/dashboard/topology-cascade.png" width="620"
     alt="Cascade topology of a real recording: dock power feeding an M3 Pro, Surface Thunderbolt 4 dock with tunnel badges, LG UltraWide merged with its hub, peripherals behind it, USB3 hub dashed as tunneled, and a dashed DisplayPort edge routed outside the tree">

## Run

Most people should never run this repo directly: both collectors compile the
built bundle into their own binary, so the dashboard arrives with them. Run
[ConnectionDoctor](../windows/README.md) `ui` on Windows or
[TBDoctor](../macos/README.md) `--serve` on macOS and open
http://localhost:8787 — the page adopts the machine serving it, so there is
nothing to configure.

To work on the UI itself:

```sh
cd dashboard
npm install
npm run dev     # then drop contract .json / events .jsonl files, or "Load fleet fixtures"
npm test        # 28 tests: contract ingest, topology collapse, layout invariants, migrations
```

`../docs/embedding.md` is the contract between this bundle and the collectors that
serve it — routes, MIME types, caching, path safety. Both producers implement
it, so the same build behaves identically on either OS.

## What works today

- **Contract ingest** — schema-gated, unknown-field tolerant, orphans flagged
  not dropped, JSONL skip-counting, evidence-mandatory findings, and "USB 2.0
  is never tunneled" enforced rather than trusted.
- **Topology view** — three layouts, physical/logical collapse, protocol
  colours, honest tunneling dashes, text-sized boxes, node inspector with
  VID:PID lookup. The layout engine is a pure-TS port of TBDoctor's, covered
  by invariant tests (no overlaps, orthogonal edges, in-canvas).
- **Timeline view** — event-derived device-count steps, root-event rules,
  incident stitching with shared-parent attribution.
- **Fleet view** — hosts side by side plus **cross-host migration detection**:
  a KVM moving a monitor's hub (and everything behind it) renders as one
  branch migration, not five device events. Count-matched for duplicate
  hardware; remove-must-precede-add.

Fixtures are real recordings from this fleet: an M3 Pro on a Surface TB4 dock
chain, and the Mac mini's actual KVM flip at 22:19:06Z.

## Next

ConnectionDoctor emits v1 and serves this bundle; TBDoctor does the same on
macOS. Remaining: a release pipeline so each collector ships a downloadable
binary, and retiring the collectors' native dashboards now that this is the UI
for both.
