# Connection Contract v1

One JSON shape for connection-diagnostic data, shared by
[TBDoctor](../macos/README.md) (macOS) and
[ConnectionDoctor](../windows/README.md) (Windows).

The two tools independently converged on the same semantics — topology,
root-vs-fallout, baseline diff, power deficit — implemented twice, in Swift and
C#. This contract freezes the *data* those semantics operate on, so improvements
stop being done twice and a single dashboard can read every machine's recording.

Conventions: JSON, camelCase, timestamps as ISO 8601 with offset, IDs as
strings. Fields marked *(opt)* may be absent; consumers must tolerate unknown
fields. Producers must not change the meaning of an existing field — additions
only; breaking changes bump the version.

## Envelope

```json
{
  "schema": "connection-contract/v1",
  "capturedAt": "2026-08-13T22:19:06-05:00",
  "host": {
    "name": "mini",
    "os": "macos",
    "arch": "arm64",
    "model": "Mac16,10"
  },
  "power": { ... },
  "nodes": [ ... ],
  "displays": [ ... ],
  "displaysKnown": true
}
```

| Field | Notes |
|---|---|
| `schema` | Literal `connection-contract/v1` |
| `host.os` | `macos` \| `windows` |
| `host.model` *(opt)* | Hardware model identifier |
| `host.id` *(opt, proposed)* | **Opaque, random, per-installation** endpoint identity (UUIDv4 generated on first run, persisted in the data directory). Survives hostname changes and normal upgrades/reinstalls (which keep the data directory); regenerates only when identity state or the data directory is reset. **Not derived from hardware** — never a hash of IOPlatformUUID / MachineGuid, which would be a global tracking identifier. Consumers key hosts on it when present, `host.name` otherwise. Portable exports replace it with a **share-scoped** pseudonym (see § Redaction and share scope) so two shared bundles are not linkable while documents inside one bundle still join. Managed-fleet correlation uses a platform-supplied endpoint ID or a tenant-keyed HMAC — fleet-integration milestone, not this field. (issue #27) |
| `displaysKnown` | `false` when the producer had no display session (SSH on macOS); distinct from "no displays attached" |
| `findings` / `incidents` / `analysis` *(opt, proposed)* | Added by `contract-findings-incidents`: `analysis: {windowHours, generatedAt, coverage}` plus the two arrays, present only when recorded history exists. Absent ≠ empty. **`coverage`** = `{availableFrom, through, complete: bool, reasons?: string[]}` — what the recorder can actually vouch for: `complete` is true only when the recording spans the whole requested window with no trim inside it and no gap longer than 3× the sample interval; `reasons` names why not (`recorder-started-inside-window`, `trimmed`, `gap`, `no-history`). Consumers show "unknown" rather than "none" whenever `complete` is false — an empty valid stream, a newly installed recorder and a trimmed log are indistinguishable without this |
| `producer` *(opt, proposed)* | `{name: "tbdoctor"\|"connectiondoctor", version, commit?, dashboard?}` — added by `release-pipeline` |

## Power

```json
{
  "source": "adapter",
  "externalConnected": true,
  "batteryPresent": true,
  "batteryPercent": 100,
  "batteryRateMilliwatts": -10500,
  "adapter": {
    "watts": 96,
    "name": "Surface Thunderbolt(TM) 4 Dock",
    "vendor": "Microsoft",
    "identifiesItself": false
  }
}
```

| Field | Notes |
|---|---|
| `source` | `adapter` \| `dock` \| `battery` \| `mains` — `dock` when the supply carries data on the same cable; `mains` for desktops |
| `batteryPresent` | Judged by capacity, not by the OS exposing a battery service (desktops can expose one with zero values) |
| `batteryRateMilliwatts` *(opt)* | Negative while discharging. **The deficit signal is `externalConnected && rate <= -2000`** — both tools use the same threshold |
| `adapter.identifiesItself` | `false` for anonymous supplies (adapter ID 0 / no manufacturer string) — the signature of dock-sourced power |
| `adapter.serial` *(opt)* | Producers should omit in exports intended to leave the machine |

## Nodes

One flat list; hierarchy expressed by `parentId`. This is the reconciliation of
macOS locationID nibbles and Windows instance IDs — both reduce to
*id + parentId*, and everything downstream (tree building, physical collapse,
grouped-loss attribution) works on that alone.

```json
{
  "id": "usb:0x00124000",
  "parentId": "usb:0x00120000",
  "kind": "hub",
  "name": "4-Port USB 2.0 Hub",
  "vendorName": "LG Electronics Inc.",
  "vidPid": "043E:9C04",
  "protocol": "usb2",
  "linkBitsPerSecond": 480000000,
  "tunneled": false,
  "usbClass": 9,
  "platform": { "locationID": 1196032 }
}
```

| Field | Notes |
|---|---|
| `id` | Stable within a snapshot. Prefix by namespace: `usb:`, `tb:`, `display:`, `host`, `power` |
| `kind` | `host` \| `thunderbolt` \| `hub` \| `device` \| `display` \| `power` |
| `vidPid` *(opt)* | Uppercase hex `VVVV:PPPP` — the cross-platform identity. Same value in IOKit and in Windows `VID_xxxx&PID_xxxx` instance IDs |
| `protocol` | Link *into* this node: `power` \| `thunderbolt` \| `displayPort` \| `usb3` \| `usb2` \| `usbLow` \| `unknown` |
| `tunneled` | Only for what USB4 genuinely tunnels (DP, USB3, PCIe). USB 2.0 is carried natively and must be `false` |
| `usbClass` *(opt)* | bDeviceClass; 9 = hub even when the name says nothing |
| `platform` *(opt)* | Untranslated native identifiers (locationID / instanceId), for debugging; consumers must not depend on it |
| `unitKey` *(opt, proposed)* | Distinguishes two units of the same VID:PID **within one collector's data**: `HMAC-SHA256(serial, installationKey)` truncated to 16 hex chars, where `installationKey` is a random secret stored beside `host.id`. Keyed per installation, so it is not linkable across machines or exports and does not expose the serial (a plain serial hash is neither redaction nor safe for enumerable serials). The raw serial never leaves the machine. Cross-endpoint unit correlation ("the same bad dock following users") is a fleet-integration concern with a tenant-scoped key. (issue #27) |

Thunderbolt/USB4 routers are nodes of kind `thunderbolt` with *(opt)*
`tb: { routeString, depth, linkGbps, firmware }`.

## Displays

```json
{ "name": "LG ULTRAWIDE", "widthPx": 3440, "heightPx": 1440,
  "refreshHz": 50, "builtIn": false, "attachedTo": "tb:..." }
```

`widthPx`/`heightPx` are native pixels, not scaled points. `attachedTo` *(opt)*
links to the node carrying the video when known.

## Events (JSONL stream)

```json
{ "t": "...", "kind": "deviceRemoved", "nodeId": "usb:0x00124100",
  "vidPid": "046D:C08A", "name": "MX Vertical" }
```

`kind`: `linkDown` | `linkUp` | `deviceAdded` | `deviceRemoved` |
`adapterChanged` | `deficitStart` | `deficitEnd` | `portError` | `fullSnapshot`.
`linkDown` from a kernel source is a **root** event; device add/remove are
usually fallout. `fullSnapshot` events embed a complete envelope as a sync point.

## Findings

```json
{
  "severity": "critical",
  "title": "Power supply under-served",
  "explanation": "...",
  "evidence": ["Battery supplied up to 10.5W while the machine reported AC power"],
  "recommendation": "...",
  "confidence": "high"
}
```

`severity`: `info` | `warning` | `critical`. Evidence is mandatory — a verdict
you cannot audit is an opinion. `confidence` *(opt)*: freeform
(`moderate`/`high`/`very high`).

## Incidents

```json
{
  "start": "...", "end": "...",
  "rootEvent": "linkDown",
  "devicesLost": [ { "vidPid": "046D:C08A", "name": "MX Vertical" } ],
  "sharedParent": "usb:0x00120000",
  "power": { "peakDischargeMilliwatts": -879 }
}
```

`rootEvent` *(opt)* names the originating event kind when one was identified;
absent means "grouped change, origin unattributed". `sharedParent` *(opt)* is
the common ancestor when the losses collapse to one — the grouped-loss finding
in data form.

## Documents

The envelope is one of three **documents** the contract defines. The other two
are the wrappers that `report --json` / `diff --json` on the CLI and the MCP
tools return, so that every JSON a person or agent sees is a document defined
here — never a per-tool shape. Each document carries `schema` and, except the
envelope, a `kind` discriminator.

| Document | `kind` | Produced by |
|---|---|---|
| **Envelope** (above) | — | `probe --json`, `tree --json`, `contract`, `GET /contract`, `connection_probe` |
| **Report** | `report` | `report --json`, `connection_diagnose`, `connection_incidents` |
| **Diff** | `diff` | `diff --json`, `connection_diff` |

### Report

```json
{
  "schema": "connection-contract/v1",
  "kind": "report",
  "host": { "name": "mini", "os": "macos", "arch": "arm64" },
  "generatedAt": "2026-08-16T22:19:06-05:00",
  "windowHours": 6,
  "findings": [ ... ],
  "incidents": [ ... ],
  "note": "recorder has not run on this machine; nothing to analyse"
}
```

| Field | Notes |
|---|---|
| `findings` *(opt)* | Array of Finding. **Absent** means not computed by this call (e.g. `connection_incidents` omits it); **`[]`** means computed and none found |
| `incidents` *(opt)* | Array of Incident, newest first; same absent-vs-empty rule |
| `note` *(opt)* | Human-readable caveat, e.g. no recording exists, or the window was truncated. Consumers show it; they do not parse it |

`connection_diagnose` returns a Report with `findings` (and may include
`incidents`); `connection_incidents` returns a Report with `incidents`; `report
--json` returns both.

### Diff

```json
{
  "schema": "connection-contract/v1",
  "kind": "diff",
  "host": { ... },
  "capturedAt": "...",
  "baselineCapturedAt": "...",
  "findings": [ ... ],
  "missing": [ { "id": "...", "kind": "hub", "name": "...", "vidPid": "043E:9C04", ... } ],
  "added": [ ... ],
  "note": "matched by instance id; vidPid+parent matching arrives with contract-conformance"
}
```

`missing` and `added` are arrays of **Node** (the node shape above), so a diff
can be rendered by the same code that renders topology. Matching identity is
`vidPid + parent's vidPid + kind` once `contract-conformance` lands on both
platforms; until then a producer that matches otherwise says so in `note`.

### Excalidraw

`excalidraw` / `connection_diagram` return an
[Excalidraw document](https://github.com/excalidraw/excalidraw/blob/master/packages/excalidraw/data/types.ts)
(`{type: "excalidraw", version: 2, source, elements[], appState}`) — an
external format, referenced not redefined here.

### Redaction and share scope

Documents that leave the machine — a support case, an issue attachment, a
bundle for a colleague — are redacted under a **share scope**. Redaction
**pseudonymises relational identity and removes non-relational identity**; it
never breaks the graph.

- **Scope token.** A generated high-entropy random token, one per bundle,
  managed by the `bundle` verb and never shown to a home user by default.
  `--scope <token>` exists so separate commands can join one bundle; a
  friendly case label is **not** a valid token (it is not the HMAC key), and
  reusing a token across bundles makes them linkable — the CLI warns.
- **Pseudonymised, consistently within the scope** (HMAC under the token,
  truncated, prefixed so the kind stays readable):
  - `host.id` and **`host.name`** (`host-3f9a…`) — a hostname such as
    `mikes-macbook` or an asset tag identifies the person as surely as an ID;
  - **every `nodes[].id`**, which embeds locationIDs / instance IDs, and every
    field that references one — `parentId`, `displays[].attachedTo`,
    `incidents[].sharedParent`, `incidents[].devicesLost[].nodeId` where
    present, event `nodeId`, diff `missing[]`/`added[]` ids — rewritten
    **recursively**, including inside every `fullSnapshot` envelope in the
    events, so topology, evidence and diffs still resolve;
  - `unitKey` is dropped (it is already scoped to the installation and adds
    nothing a recipient can use).
- **Removed** recursively: `platform{}`, raw serials, `adapter.serial`, and any
  other field the schema marks as native or personal.
- **Names.** Product strings from device descriptors (`4-Port USB 2.0 Hub`,
  `MX Vertical`) are model identity and are kept with `vidPid`/`vendorName`.
  Names that the OS reports as **user-assigned** — display names, Bluetooth and
  Apple device names such as `Mike's iPhone`, renamed peripherals — are
  replaced by a conservative label built from evidence that stays
  (`<vendorName> <kind>` or `<vidPid>`), and the node gains
  `nameRedacted: true`. Producers classify which name fields are user-assigned
  per platform; the manifest lists them.
- **Manifest.** Every bundle carries `manifest.json`: scope pseudonym for the
  host, the list of fields transformed and removed, counts of names replaced,
  the documents included and their coverage — so the user can see exactly what
  they are about to share, before they share it.
- `contract --redact` / `report --redact` / `diff --redact` on their own use an
  implicit one-document scope. `bundle <out.zip> [--hours N] [--scope token]`
  produces the envelope, the report, the events window and the manifest under
  one scope.
- The identity state itself (`identity.json`: `host.id`, `installationKey`)
  never leaves the machine and is not part of any bundle.

Tests a redacted bundle must pass: validates against the JSON Schema; every
reference resolves; the topology (tree shape, kinds, protocols, vidPids) is
unchanged from the unredacted source; no original id, hostname or serial
substring survives anywhere in the archive; two bundles from one machine do
not correlate; documents inside one bundle do.

### Machine-checkable schema

`docs/schema/v1/` will hold JSON Schema files for the envelope, report and diff
documents (`contract-conformance` task); the dashboard's parser tests and the
conformance tests validate wrappers and elements against them, so a missing
field or renamed key fails a test rather than passing silently.

## Source mapping

| Contract field | macOS (TBDoctor) | Windows (ConnectionDoctor) |
|---|---|---|
| `nodes[].id` / `parentId` | `locationID` + nibble parent walk | `InstanceId` + `CM_Get_Parent` |
| `nodes[].vidPid` | `idVendor`/`idProduct` | `VID_xxxx&PID_xxxx` in instance ID |
| `nodes[].tb` | `IOThunderboltSwitch*` | USB4 router devnodes |
| `power.batteryRateMilliwatts` | `InstantAmperage × Voltage` | WMI `BatteryStatus` charge/discharge rate |
| `power.batteryPresent` | `MaxCapacity > 0` | battery device with nonzero capacity |
| events `linkDown` | `log stream` "unplug on primary lane" | ETW USBHUB3/UCX (future); poll-derived until then |

## Versioning

`connection-contract/v1`. Additive changes only within v1; anything that
changes meaning is v2. Consumers select on the `schema` field.
