# Design: windows-event-ingest

## Two inputs, one recorder
The recorder becomes an event merger: a notification channel (immediate,
timestamped by the OS) and the poll channel (every 5 s, tightening to 1 s for
a minute after any notification — the same rule TBDoctor uses after a kernel
event). Both write to the same JSONL through `Recorder`, which owns dedup.

## Dedup rule
Key = `(nodeId, kind)`. A poll-derived add/remove is dropped if a
notification-derived event with the same key exists in the current poll
interval; a notification never dropped. Ordering in the file is by event
timestamp, not arrival; the poll's `fullSnapshot` remains the sync point.

## What CM_Register_Notification gives us
Interface arrival/removal for a device-interface class GUID with the instance
path — enough for `deviceAdded`/`deviceRemoved` with the contract node id and,
after a targeted re-probe of that node, its `vidPid`/name. Reset-and-recover
inside 5 s becomes a removed/added pair with real timestamps.

## What ETW gives us, and its cost
USBHUB3/USBXHCI/UCX carry port reset, connect/disconnect and error events with
port and device context — the root event when a hub resets. Consuming ETW
needs a real-time session (`TraceEventSession`, admin for some providers) —
so phase 2 is opt-in and degrades honestly: without the session, `linkDown`
is still absent on Windows and the report says so, rather than being faked
from device loss.

## Ambiguity stays ambiguous
A `deviceRemoved` burst with no kernel event remains an unattributed incident
(`rootEvent` absent) — the dashboard shows "grouped loss, origin unattributed",
matching the schema's stated semantics.
