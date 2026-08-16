# Design: add-windows-mcp

## Shape
Port `MCPServer.swift` one-to-one: read lines from stdin, each a JSON-RPC
request; dispatch `initialize` / `tools/list` / `tools/call` / `ping`; write
one JSON line per response to stdout; log to stderr only. No framework — the
protocol surface used is small and a dependency would be the largest thing in
the binary.

`tools/list` is served from the embedded `docs/mcp-tools.json`; a test asserts
the embedded copy equals the repo file so descriptions cannot drift from macOS.

## Findings shape now vs later
`connection_diagnose` returns what the Windows analysis can say today
(baseline-diff findings when a baseline exists, the power-deficit finding),
already in the schema `Finding` shape with a non-empty `evidence[]` — which
means adding `Evidence` to the C# `Finding` record now rather than waiting.
When `contract-findings-incidents` lands the ranked engine, this tool's shape
does not change.

## Diagram on Windows
Options: (a) port `ExcalidrawExport.swift` + `Diagram.swift` layout to C#
(~600 LOC, a fourth copy of the layout engine); (b) return the envelope and let
the dashboard export; (c) run the dashboard's TS layout in-process. (a) is
rejected on principle (see `docs/architecture.md`: analysis/layout copies are
the drift). Decision: **`connection_diagram` on Windows is delivered by the
dashboard's Excalidraw export** (a small addition to the dashboard: `POST`-less,
pure function `toExcalidraw(envelope, style)` in `dashboard/src/domain`) and
the Windows tool returns `isError` with a pointer until that exists — tracked
in `contract-conformance` where the layout copies converge. This keeps the tool
*present* with identical schema on both platforms while not adding a copy.

## Timeouts
`DeviceProbe.Capture()` can take seconds on a busy hub; the tool answers within
10 s or returns partial state with a `note`, per `docs/mcp.md`.
