# Connection Contract v1

One JSON shape for connection-diagnostic data, shared by
[TBDoctor](https://github.com/mhuot/tbdoctor) (macOS) and
[ConnectionDoctor](https://github.com/mhuot/connectiondoctor) (Windows).

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
| `displaysKnown` | `false` when the producer had no display session (SSH on macOS); distinct from "no displays attached" |

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
