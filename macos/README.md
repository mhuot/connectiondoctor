# TBDoctor

Finds the root cause of Thunderbolt dock, USB peripheral and power faults on macOS —
the "my dock keeps disconnecting" and "my devices randomly drop" class of problem.

Built after a CalDigit Element Hub kept dropping a keyboard, mouse, monitor hub, mic
and two cameras. The cause turned out to be a power supply that could not cover the
laptop's demand: when demand exceeds supply, USB-C power delivery renegotiates, that
renegotiation resets the port, and a port reset tears down the Thunderbolt link and
everything behind it. Nothing on macOS correlates the four data sources needed to see
that, so this does.

## Why a tool

The failure is intermittent and hits while you are busy — mid-call, mid-build. By the
time you look, it is over. So TBDoctor records continuously and reconstructs the fault
afterwards, rather than being a dashboard you have to already be watching.

It also does the part that is genuinely hard: telling a **root** event from its
**fallout**. One dropped Thunderbolt link produces hundreds of downstream USB errors,
and reading those logs naively points you at whichever innocent device happens to be
noisiest. (While diagnosing the original fault by hand, that trap produced two wrong
answers before the right one.)

## What it detects

| Finding | Signature |
|---|---|
| Power supply under-served | Battery discharging while the machine reports AC power |
| Power-induced link drop | A Thunderbolt link drop landing inside a power-deficit window |
| Cable / signal integrity | Link drops with *no* deficit nearby, often at a regular sub-second cadence |
| Grouped device loss | Several USB devices vanishing together behind a shared hub |
| Supply arbitration | The active adapter's identity flipping repeatedly between two sources |
| Headroom warning | Peak demand at or above the adapter's rating, before anything fails |

Every finding carries the evidence that produced it. A verdict you cannot audit is
just an opinion, and these failure modes are easy to get confidently wrong.

## Install

```sh
./build_app.sh
open TBDoctor.app
```

Add it to Login Items (System Settings → General → Login Items) so it is recording
before you need it.

## Menu bar

A status symbol — shape-coded as well as colour-coded, so it stays readable in a
monochrome menu bar. Click for current Thunderbolt link, adapter identity and
wattage, battery current, USB device count, the leading root cause, and the last
incident. "Open timeline…" gives charts of link state, power and device count with
root events marked, alongside the full findings list.

## Command line

```sh
TBDoctor.app/Contents/MacOS/TBDoctor --probe     # current state, once
TBDoctor.app/Contents/MacOS/TBDoctor --tree      # connection tree + where power enters
TBDoctor.app/Contents/MacOS/TBDoctor --report    # analyse history, print findings
TBDoctor.app/Contents/MacOS/TBDoctor --watch     # live one-line-per-sample table
TBDoctor.app/Contents/MacOS/TBDoctor --inspect samples.jsonl   # draw a recorded tree
```

`--inspect` opens the connection tree for a *recorded* sample instead of live
hardware, so you can look at a capture from another machine. A dock that
misbehaves on one Mac and not another is a common shape for this fault, and the
two trees side by side are the fastest way to see why.

## Connections

A resizable window (menu bar → Connections…, or the button in the timeline)
drawing the physical topology as boxes joined by right-angle connectors: power
source, host, Thunderbolt device, then the USB hierarchy rebuilt from
location-ID nibbles. It exists because this is the part people misread — a
monitor with a built-in hub looks like infrastructure, and the devices behind it
look directly attached.

Three layouts, switchable and remembered between launches:

| Layout | Shape | Good for |
|---|---|---|
| **Cascade** | Each child steps down and right | Narrow; grows downward. The default. |
| **Top-down** | Children fan out below, power enters from the left | Reads as a schematic |
| **Flow** | Left to right: power, Mac, dock, hubs, devices | Matches how you'd describe a dock chain |

The power path is drawn in amber and separately from the data tree, so where
power enters is never confused with what carries data. Boxes are sized to their
text rather than fixed — truncating "4-Port USB 2.0 Hub — LG Electronics Inc."
throws away the one word identifying the hardware. Hubs that self-describe as
"Generic" are resolved by matching their vendor ID against their own children,
which is how that anonymous hub becomes identifiably the LG monitor's.

`--tree` prints the same topology as text.

Note that IOKit exposes no per-device power draw on current hardware, so the
diagram shows where power *enters* and which nodes are consumers — it does not
invent per-device wattages.

## Use from a coding agent

TBDoctor is an MCP server, so Claude Code, Claude Desktop, Copilot in VS Code and
Cursor can query the hardware directly instead of being handed pasted terminal output.

```sh
claude mcp add tbdoctor -- /full/path/to/TBDoctor.app/Contents/MacOS/TBDoctor --mcp
```

Tools: `tb_probe` (current state), `tb_diagnose` (ranked findings with evidence),
`tb_incidents` (reconstructed fault history).

## How it works

Sampled from IOKit every 5s, tightening to 1s for a minute after any kernel event —
resolution matters exactly when something is happening.

| Source | Read |
|---|---|
| `IOThunderboltSwitch*` | Attached devices, vendor, model, depth, route |
| `IOThunderboltPort` | `Link Bandwidth` in units of 0.1 Gb/s |
| `IOPSCopyExternalPowerAdapterDetails` | Adapter watts, ID, serial, manufacturer |
| `AppleSmartBattery` | `ExternalConnected`, `InstantAmperage`, `Voltage` |
| `IOUSBHostDevice` | Device tree with negotiated speeds |
| `log stream` | Link and port kernel events |

Data lives in `~/Library/Application Support/TBDoctor/` as JSONL, trimmed at 24MB.
Override with `TBDOCTOR_DIR`.

## Implementation notes

Things that were not obvious, recorded so they are not re-learned the hard way:

- **Thunderbolt switches do not share one class name.** The host controllers here are
  `IOThunderboltSwitchType7`; the attached CalDigit dock is
  `IOThunderboltSwitchIntelJHL8440` — named after the *device's* controller silicon.
  Matching an enumerated list of class names silently misses hardware, so TBDoctor
  matches the substring `ThunderboltSwitch`. This bug made the dock invisible.

- **`ioreg` renders `InstantAmperage` as an unsigned 64-bit wrap.** A 4.4A discharge
  prints as `18446744073709547204`. IOKit stores it signed; read it via `int64Value`
  and it is simply `-4412`.

- **The adapter drops out *during* a fault.** An earlier deficit detector required the
  adapter to be continuously present and so broke its run at exactly the diagnostic
  moments — the fault read as healthy. Runs now tolerate gaps and are qualified as a
  whole.

- **`log stream` drops messages under burst load**, which is precisely when a fault is
  happening. The predicate is kept narrow and a periodic `log show` sweep backfills.

- **`log` logs itself.** Running a query whose predicate contains "overcurrent" puts
  the word "overcurrent" in the log. This produced a phantom finding during the
  original investigation; those lines are now filtered.

- **Total demand is an upper bound.** It is the adapter's *rating* plus the battery's
  contribution, and a struggling adapter may not be delivering its rating. The
  battery contribution is measured; the total is labelled as inferred.

## Limits

- Link speed is reported per-link, taking the fastest active port. Exact for a single
  dock; a daisy chain reports the fastest hop rather than per-device speeds.
- Ad-hoc signed. Fine locally; needs a Developer ID identity to distribute.
- Only tested against the hardware it was written on: an M5 Pro MacBook Pro and a
  CalDigit Element Hub. The class-name matching above is the main portability risk.
