# ConnectionDoctor

Finds the root cause of dock, USB, display and power faults — the "my dock keeps
disconnecting" and "my monitor's hub is dead but the picture is fine" class of
problem — on macOS and Windows, with one dashboard for both.

| | What | Where |
|---|---|---|
| **TBDoctor** | macOS collector: menu-bar app, CLI, MCP server (Swift, IOKit) | [`macos/`](macos/README.md) |
| **ConnectionDoctor** | Windows collector: tray, CLI, MCP server (C#, SetupAPI/CfgMgr32) | [`windows/`](windows/README.md) |
| **Connection Dashboard** | The UI for both — topology, timeline, fleet (React) | [`dashboard/`](dashboard/README.md) |
| **Connection Contract v1** | The JSON shape both collectors emit and the dashboard reads | [`docs/schema-v1.md`](docs/schema-v1.md) |
| **Embedding contract** | How a collector serves the dashboard bundle | [`docs/embedding.md`](docs/embedding.md) |
| **CLI / MCP contracts** | One verb set and one MCP tool set for both binaries *(proposed)* | [`docs/cli.md`](docs/cli.md) · [`docs/mcp.md`](docs/mcp.md) |
| **Architecture** | One binary, three doors, one in-process core; what is shared and how | [`docs/architecture.md`](docs/architecture.md) |
| **Distribution** | Release artifacts, signing, CI *(proposed)* | [`docs/distribution.md`](docs/distribution.md) |

Both collectors record continuously, emit the same **Connection Contract v1**,
and compile the dashboard bundle into their own binary. A user downloads one
file per machine and opens `http://localhost:8787`; the page adopts the machine
that serves it and other machines can be added by address, so a dock that moves
between hosts is diagnosed as one fleet.

## Use it

```sh
# macOS
macos/build_app.sh && open macos/TBDoctor.app          # menu bar app, records at login
macos/TBDoctor.app/Contents/MacOS/TBDoctor --serve      # dashboard on http://localhost:8787
```

```powershell
# Windows
dotnet build .\windows\ConnectionDoctor.sln
.\windows\artifacts\win-arm64\ConnectionDoctor.exe install   # record now and at login
.\windows\artifacts\win-arm64\ConnectionDoctor.exe ui        # dashboard in the browser
```

## Build the dashboard into the collectors

Node is needed only by whoever cuts a release — never by a user.

```sh
scripts/build-ui.sh            # or scripts/build-ui.ps1 — builds dashboard/, stages into both collectors
macos/build_app.sh             # then rebuild the collector(s)
dotnet build windows/ConnectionDoctor.sln
```

A collector built with nothing staged still runs; `/` explains that instead of
serving a UI.

## Work on the dashboard

```sh
cd dashboard
npm ci
npm run dev     # drop contract .json / events .jsonl files, or "Load fleet fixtures"
npm test
```

## Layout

```
dashboard/   React + Vite app (src/contract = v1 ingest, src/domain = layout/topology/incidents, src/components = views)
macos/       SwiftPM package → TBDoctor.app  (Sources/TBDoctor/ui is the staged bundle, git-ignored)
windows/     .NET 8 solution → ConnectionDoctor.exe (src/ConnectionDoctor/ui is the staged bundle, git-ignored)
docs/        schema-v1.md, embedding.md, images/
scripts/     build-ui.sh / build-ui.ps1
openspec/    spec-driven change history (OpenSpec); .claude/ holds the /opsx commands
```

## Roadmap

Spec-driven with [OpenSpec](https://github.com/Fission-AI/OpenSpec). The open
changes under [`openspec/changes/`](openspec/changes/) are the plan, in order:
`add-windows-mcp` · `align-cli-verbs` (their interface contracts,
`define-interface-contracts`, are reviewed and archived — `docs/cli.md`,
`docs/mcp.md`); `contract-findings-incidents` → `macos-resident-serves-dashboard`;
`contract-conformance`; `release-pipeline`; `windows-event-ingest`. Each has a proposal, design, spec
deltas and tasks. Managed-fleet work is a later, optional layer — milestone
`fleet-integration`, boundary recorded in `docs/architecture.md`.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE). Copyright 2026 Mike Huot.

History: this repo was assembled from three repositories — `mhuot/connectiondoctor`,
`mhuot/tbdoctor`, `mhuot/connection-dashboard` — with `git subtree`, so every
original commit is here (`git log -- macos/`, `git log -- dashboard/`).
