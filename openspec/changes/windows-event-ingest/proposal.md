# Proposal: windows-event-ingest

## Why
The Windows collector samples every five seconds and derives events only by
comparing adjacent snapshots (`BackgroundCollector` → `Recorder`). A dock or
USB reset that fails and fully recovers inside that interval leaves two
identical snapshots and **no record at all** — the incident is absent, not
late. And because Windows has no OS-event input, it can never emit the
contract's `linkDown` / `linkUp` / `portError`; its incidents are all fallout
(`deviceRemoved`) with no root event, which is exactly the trap the whole
product exists to avoid (issue #37; README milestone 2).

## What
1. **Device notifications first (small, no ETW session).**
   `CM_Register_Notification` for device-interface arrival/removal (USB, HID,
   USB4, monitor classes) with the OS timestamp; each becomes a
   `deviceAdded` / `deviceRemoved` event immediately, tagged
   `source: "notification"`. Polling continues for state reconciliation and
   startup recovery.
2. **Link and port events second.** ETW consumers for
   `Microsoft-Windows-USB-USBHUB3`, `-USBXHCI`, `-USB-UCX` (and USB4/UCSI where
   present) mapped to `linkDown` / `linkUp` / `portError` with `source:
   "kernel"`. Only when the provider evidence supports it; ambiguous loss stays
   unattributed.
3. **Dedup and ordering.** A notification-derived event and the next poll's
   diff for the same node within one interval are one transition: the
   notification wins (earlier, more precise timestamp), the poll only fills
   what notifications missed. Rules written in the spec and tested.
4. **Dashboard root-event handling.** `incidents.ts` treats `linkDown` as the
   root; extend to `portError`, and use `source` to rank kernel above poll.
   Windows incidents gain root events for the first time.
5. **Integration test:** reset + recover in < 5 s (a scripted `pnputil
   /disable-device` … `/enable-device` on a hub) → the incident is recorded
   with ordered down/up events and a root event.

## Non-goals
Modern Standby / lid-action awareness (README milestone 4 — a separate
change); macOS changes (its `log stream` path already does this).

## Impact
Capability `windows-events` (new); `timeline-view` (root-event kinds);
Windows `BackgroundCollector.cs`, `Recorder.cs`, new `DeviceNotifications.cs`,
`EtwListener.cs`, `ContractV1.cs` (`source` on events); `docs/schema-v1.md`
(`source: kernel|poll|notification` on link and device events — the
`contract-conformance` rule generalised); dashboard `incidents.ts`.

## Depends on
`contract-conformance` (event `source` field and the incident fixtures it adds
are where this lands its tests). Issue #37.
