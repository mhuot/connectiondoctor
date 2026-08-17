# Architecture

One product, two collectors, three interfaces, one dashboard. This page records
the decisions that keep macOS and Windows the same to use; the OpenSpec changes
under `openspec/changes/` are the work that gets each platform there.

## One binary, three doors

Each platform ships **one binary** — `TBDoctor.app` on macOS,
`ConnectionDoctor.exe` on Windows — with three ways in:

| Door | Who uses it | Transport | Spec |
|---|---|---|---|
| **CLI** | a person in a terminal, scripts, SSH | argv in, text (or `--json`) out | [`docs/cli.md`](cli.md) |
| **MCP server** | coding agents (Claude Code, Cursor, Copilot) | stdio JSON-RPC | [`docs/mcp.md`](mcp.md) |
| **HTTP + dashboard** | a browser, on this machine or the LAN | `http://localhost:8787` | [`docs/embedding.md`](embedding.md) |

All three are thin front-ends over the **same in-process core**. None of them
shells out to another; the CLI is a door, not a floor.

```
 terminal            coding agent           browser
    │ argv/stdout        │ stdio JSON-RPC       │ http://localhost:8787
    ▼                    ▼                      ▼
 ┌─────────┐        ┌────────────┐        ┌───────────────────────┐
 │  CLI    │        │ MCP server │        │ HTTP + dashboard bundle│
 └────┬────┘        └─────┬──────┘        └───────────┬───────────┘
      └───────────────────┼───────────────────────────┘
                          ▼   in-process calls
 ┌───────────────────────────────────────────────────────────────┐
 │ core                                                          │
 │   Analysis   findings · incidents · root-vs-fallout · diff    │
 │      ▲                       ▲ history                        │
 │   Probe ──emits──▶ Contract v1 ──every 5 s──▶ Recorder ──▶ JSONL│
 │      ▲                                                        │
 └──────┼────────────────────────────────────────────────────────┘
   IOKit / SetupAPI+CfgMgr32          ~/Library/… or %LOCALAPPDATA%\…
```

**Decision (2026-08-16): shared in-process core, not CLI-as-substrate.**
The alternative — MCP and the HTTP server spawning the CLI and parsing its
output — was considered and rejected: it adds a process per call, turns the
CLI's text format into a load-bearing API, and requires every function to be
exposed on the CLI first. Both codebases already have the in-process shape
(Swift: `Headless`/`MCPServer`/`Serve` over `Probes`/`Collector`/`Diagnosis`;
C#: `Program`/`ContractServer`/`TrayApplication` over `DeviceProbe`/`Recorder`/
`SnapshotComparer`); this decision keeps it.

## The resident process

The **menu-bar app** (macOS) and **tray** (Windows) are the same thing: a
status light, a launcher, the process that runs the **Recorder**, and the
process that **serves the dashboard** for the whole login session — so the URL
is simply always there. They are not a second UI: the views live in the
dashboard, once.

The only real runtime dependency between doors is on the Recorder's data. Live
probing works with nothing resident; timeline, incidents and findings need the
JSONL the resident process keeps writing. Every door says so honestly when the
recording is absent.

## What is shared, and how

The core is written twice — Swift and C# — and only the **Probe** layer has
to be (IOKit vs SetupAPI). Everything above it is logic over Contract v1 data.
Sharing therefore happens through **specifications and fixtures**, not shared
code:

| Layer | macOS | Windows | What keeps them equal |
|---|---|---|---|
| CLI + MCP | `Headless.swift`, `MCPServer.swift` | `Program.cs`, MCP (to write) | `docs/cli.md`, `docs/mcp.md` + golden-output tests |
| Dashboard | one React bundle, `dashboard/`, embedded in both | | `docs/embedding.md` — already one implementation |
| Analysis | `Diagnosis.swift`, `Collector.swift` | `SnapshotComparer.cs`, `PowerDiagnosis.cs` (+ TS copy in `dashboard/src/domain`) | **conformance fixtures**: same recorded input → same findings/incidents JSON |
| Contract v1 | `Contract.swift`, `ContractLog.swift` | `ContractV1.cs`, `Recorder.cs` | `docs/schema-v1.md` |
| Probe | `Probes.swift` → IOKit | `DeviceProbe.cs` → SetupAPI | two by necessity |

Two structural rules follow:

1. **Analysis reads Contract v1, not native models.** Once both analysis layers
   consume the envelope/event shapes rather than `Sample` / `ConnectionSnapshot`,
   one folder of recorded fixtures can assert that Swift, C# and TypeScript
   produce identical findings. This is how two implementations stay honest
   without a shared runtime.
2. **Anything a user or agent can observe is specified.** Contract, embedding,
   CLI verbs and outputs, MCP tool names and schemas, install story. If it is
   not in `docs/`, the two platforms will drift on it.

The bolder option — one analysis core in Rust (static lib for Swift/C#, wasm
for the dashboard) — is deferred until conformance tests show the
two-implementation cost is actually biting.

## Fleet integration (later) — the boundary

A second review (issues [#26](https://github.com/mhuot/connectiondoctor/issues/26),
[#28](https://github.com/mhuot/connectiondoctor/issues/28),
[#30](https://github.com/mhuot/connectiondoctor/issues/30),
[#32](https://github.com/mhuot/connectiondoctor/issues/32),
[#33](https://github.com/mhuot/connectiondoctor/issues/33), milestone
`fleet-integration`) asked what a managed endpoint team would need: durable
fleet history, a triage queue across machines, Intune/Jamf lifecycle,
authenticated transport, SIEM/service-desk hand-off. The decision:

- **The local-first path is the product.** One download, no account, no
  cloud, the dashboard opens itself, a few trusted LAN hosts added by hand if
  wanted, full diagnosis offline. Anything managed is an **optional layer with
  progressive disclosure** and adds no setup step or enterprise vocabulary to
  that path.
- **Complement, don't recreate.** No ConnectionDoctor control plane. The
  managed layer is a *bounded export* — the **report document**
  (`schema-v1.md` § Documents) plus stable endpoint identity, freshness and
  collector version — delivered through the platforms teams already run
  (management-platform inventory/remediation output, log/OTel, scheduled
  support bundles), with connector recipes rather than a service. Summarised
  and privacy-filtered by default; raw event history stays on the machine.
- **MCP stays local-only:** stdio, read-only, no fleet credentials, no remote
  enrollment. It is a door for a coding agent on this machine, not a transport.
- **It starts after the seven changes below**, because everything it would
  export is produced by them (`host.id` and freshness from
  `contract-conformance` / `contract-findings-incidents`, `producer{}` from
  `release-pipeline`). Filed, labelled `scope:fleet-integration`, not designed.

Three items from that review were small enough and local-first enough to fold
into the changes below: per-host freshness and history completeness (#29 →
`contract-findings-incidents`), negative-control fixtures and parity-vs-quality
tests (#31 → `contract-conformance`), and stable `host.id` / hashed serials
(#27 → `contract-conformance`).

## Order of work

```
1 interface specs (cli.md, mcp.md) ──▶ 2 Windows MCP server
                                   └─▶ 5 CLI verb alignment
4 findings[]/incidents[] in the envelope + dashboard findings panel
                                   ──▶ 3 macOS resident process serves the dashboard; retire native windows
6 conformance fixtures → drift fixes  (informed by 2, 3, 5)
7 release pipeline (docs/distribution.md)
```

`1 → 2` and `4 → 3` are the only hard orderings; 1 and 4 start together. Each
number is an OpenSpec change under `openspec/changes/`.
