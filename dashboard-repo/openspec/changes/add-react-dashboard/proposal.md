# Proposal: add-react-dashboard

## Why

Two diagnostic tools now exist — TBDoctor (macOS, SwiftUI) and ConnectionDoctor
(Windows, WPF) — with independently implemented UIs over the same semantics.
Every UI improvement is done twice, and neither UI can answer the question this
fleet actually generates: *where did my devices go?* Four machines share one
dock and a KVM'd monitor; peripherals migrate between hosts several times a
day. Today, answering "how does the mini look?" means SSHing into each machine.

Connection Contract v1 (the shared data schema, committed in
`mhuot/tbdoctor docs/schema-v1.md`) exists precisely to enable one UI over both
tools' data. This change builds that UI.

## What

A React + TypeScript dashboard that renders Connection Contract v1 data:

1. **Contract ingest** — load, validate and normalize v1 envelopes and event
   streams from files (drag-drop / picker) and from collector HTTP endpoints
   when available. Tolerant of unknown fields per the contract's rules.
2. **Topology view** — the connection diagram (cascade / top-down / flow
   layouts, physical/logical toggle, protocol-coloured links, tunneling
   dashes, node inspector) as React components over a pure TypeScript layout
   engine ported from TBDoctor's Diagram engine.
3. **Timeline view** — link state, power, device count charts with root events
   marked; findings panel with evidence; incident list.
4. **Fleet view** — multiple hosts side by side, with device-migration
   detection: the same vidPid disappearing on one host and appearing on
   another within a window renders as a migration, not two incidents.

## Non-goals

- Replacing the native menu bar / tray presences. They stay; this is the
  deep-dive surface they link to.
- Collector work. TBDoctor and ConnectionDoctor emit the contract under their
  own issues (tbdoctor#1, connectiondoctor#15); until then the dashboard runs
  on fixture files converted from real recordings.
- Shell packaging (Tauri) in this change. The app is web-first and must not
  assume shell APIs; wrapping is a follow-up change.

## Approach decision (recorded)

React Native was evaluated and **rejected**: both platform forks lag core RN,
tray/menu-bar chrome is not first-class on either, and the components RN would
share are the cheap part of these UIs. Chosen: **web React (Vite), wrapped by
Tauri in a later change**. Full reasoning in design.md.

## Impact

- New codebase (this repo). No changes to TBDoctor or ConnectionDoctor beyond
  their existing contract-emission issues.
- New capabilities: `contract-ingest`, `topology-view`, `timeline-view`,
  `fleet-view`.
