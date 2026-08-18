# Hardware verification kit

Everything this project has got badly wrong on real hardware has been
**format-valid and semantically false**: a location token that parses as a
serial, one `unitKey` shared by three unrelated devices, a laptop panel
classified as external because its name is "Generic PnP Monitor". Every one of
those passed CI, passed schema validation, and would pass a checklist that asks
"did the tool run?"

So this kit does two things a person cannot reliably do by hand.

1. **Records what was observed, with provenance attached.** The producer names
   itself in the `Server` header of every response, so the build under test is
   read rather than transcribed.
2. **Compares that observation against ground truth you declared in advance.**
   The rig file is the important half: it turns "does this look right?" — a
   judgement — into "does this match what I said is on my desk?" — a
   comparison.

`UNKNOWN` is a first-class result and is not success. A kit that reports PASS
for hardware that was not attached is worse than no kit, because it retires a
question without answering it.

## Setup

Needs `bash`, `curl` and `jq`. On Windows, Git Bash provides the first two;
`winget install jqlang.jq` provides the third. On macOS, `brew install jq`.

```sh
cp docs/hardware/rig.example.json ~/my-rig.json   # then describe your desk
```

Start the collector serving on a port of its own, against a data directory of
its own, so a test run never disturbs real recorded history:

```sh
# Windows
$env:CONNECTIONDOCTOR_DIR="$env:TEMP\hwkit"; .\ConnectionDoctor.exe serve 8793
# macOS
TBDOCTOR_DIR=/tmp/hwkit ./TBDoctor --serve 8793
```

```sh
export HWKIT_ENDPOINT=http://127.0.0.1:8793
```

## The standing invariants

These run on every capture, with no rig file and no ceremony. Each one is a bug
we have already shipped once — that is the whole point of writing them down:
curiosity found them, and it does not have to find them again.

| Check | The failure it remembers |
|---|---|
| `host.id` is a random UUIDv4 | A v1 UUID encodes a MAC address, which is the tracking identifier this field exists to avoid |
| `host.id` identical across captures | An id that changes between runs splits one endpoint into many |
| keyed nodes == distinct keys | Three unrelated built-in nodes once shared one `unitKey` |
| every keyed node has a VID:PID | A serial alone is not a unit; two products reporting `0001` collapsed into one |
| declared serials appear nowhere | The raw serial must never leave the machine |

The summary line prints keyed **and** distinct counts on every capture, pass or
fail. The retimer collision was only visible because someone printed both.

## The procedure

Run the steps in order. Everything in `[you]` is physical; everything in
`[kit]` is a command.

### A. Baseline and identity durability

```
[kit]  ./scripts/hwkit.sh capture baseline
[you]  stop the collector, start it again with the same data directory
[kit]  ./scripts/hwkit.sh capture after-restart
```

### B. Built-in panel detection — issue #14

Every criterion on #14 is hardware-observable and none has been verified. This
is the section that closes it.

```
[you]  attach at least one external monitor
[kit]  ./scripts/hwkit.sh capture displays-attached
[you]  power the external monitor OFF, leave it plugged in, wait 15s
[kit]  ./scripts/hwkit.sh capture monitor-powered-off
[you]  power it back on, wait 15s
[kit]  ./scripts/hwkit.sh capture monitor-back
```

Expected: the internal panel is `builtIn: true` in all three; the external
monitor is `builtIn: false` while present. When powered off it should leave the
document, **not** flip to `builtIn: true` or appear as internal —
`QueryDisplayConfig` reports active targets only, so an absent monitor is
unknown rather than embedded. This is where a struct-layout mistake in the
P/Invoke shows up as plausible-but-wrong data instead of a crash.

```
[kit]  ./scripts/hwkit.sh compare displays-attached monitor-powered-off
```

### C. Unit identity under replug

```
[you]  unplug the dock, wait 15s
[kit]  ./scripts/hwkit.sh capture dock-out
[you]  replug it, wait 30s
[kit]  ./scripts/hwkit.sh capture dock-back
```

Expected: the dock and everything behind it leave and return; the dock's
`unitKey` in `dock-back` is **identical** to `baseline`. A key that changes
across a replug means the field cannot do the job it exists for.

### D. Verify and record

```
[kit]  ./scripts/hwkit.sh verify ~/my-rig.json
[kit]  ./scripts/hwkit.sh report ~/my-rig.json
```

`report` writes `report.md` into the run directory. Attach that to the issue
being verified. Run directories are git-ignored because raw contracts contain
device-scoped instance IDs; the report is the shareable artefact.

## What this kit cannot do

Stated plainly, because a test harness that hides its limits is the thing it is
supposed to prevent.

- **Sub-poll events (#37).** A dock reset that completes between five-second
  samples cannot be produced by hand reproducibly. That needs programmable
  switching — a locally-controlled smart plug to cut dock mains for under a
  second, and a USB-C PD load tester to force a genuine sustained deficit
  rather than simulate one. Not bought yet, deliberately: worth deciding once
  the manual stages show which assertions actually stay unreachable.
- **Cross-endpoint unit correlation.** Two `unitKey`s from two machines are
  keyed by different installation secrets and cannot be compared. That is the
  privacy property working, and it is fleet-integration scope.
- **Curiosity.** Nobody told the process that found the retimer collision to
  compute "5 keyed, 3 distinct". The invariants above are its findings, not its
  method. When something here reads oddly and no check fires, that is worth an
  issue rather than a shrug.
