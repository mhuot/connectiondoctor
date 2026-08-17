# Proposal: dashboard-topology-controls

## Why
Two findings from testing the live dashboard on a Surface Laptop 7 (issues
#42, #43):
- Switching **Physical → + logical** can look like it did nothing: the first
  surfaced node sits below the fold (49 nodes surfaced across 13 containers,
  first at y≈1300), and "logical" names a category the contract does not
  have — the mode is "every exported node" versus "folded".
- There is no **Include built-in devices** control: the Windows envelope
  always includes the integrated panel, touch screen, touchpad and internal
  keyboards, so a laptop's external topology is buried in its own internals.
  The old WinForms view had this toggle; the React one never got it.

## What
- **Contract:** additive `nodes[].builtIn?: boolean` — the producer's
  classification (Windows `DeviceFilters` already knows; macOS marks Apple
  internal keyboard/trackpad/camera/ambient light and the built-in display's
  hub) so the dashboard never guesses from names (#14 keeps improving the
  Windows classification itself). Nodes are always exported; filtering is a
  view choice, so `fullSnapshot` sync points and refresh stay consistent.
- **Dashboard:** rename the mode radio to **Physical / All device nodes**;
  a chip next to it reads "N internal nodes folded into M containers" or
  "N surfaced" so a switch is visible without scrolling; add **Include
  built-in devices** (default **off** — hide integrated devices, keep every
  external dock/display/USB branch), persisted like the layout choice; the
  inspector shows `builtIn` when set.
- Tests: mode switch on a fixture whose first folded branch is below the
  viewport asserts the chip changes; built-in filter on a Surface-class
  fixture hides panel/touch/internal HID and keeps the LG UltraWide and TB4
  dock branches; producer tests for `builtIn` on both platforms.

## Non-goals
Changing Physical's fold rules or the `+N internal` accounting; server-side
filtering (`?builtIn=` query) — the contract carries the flag, the view decides.

## Impact
`docs/schema-v1.md` (node field); `Contract.swift`, `ContractV1.cs`
(`builtIn`); dashboard `TopologyView.tsx`, `topology.ts` (filter), inspector;
capabilities `topology-view`, `contract-v1`.
