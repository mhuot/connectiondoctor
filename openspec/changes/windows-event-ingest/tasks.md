# Tasks: windows-event-ingest
- [ ] 1.1 `docs/schema-v1.md`: `source: kernel|poll|notification` on link and device events; dedup rule stated
- [ ] 1.2 `DeviceNotifications.cs`: `CM_Register_Notification` for USB/HID/USB4/monitor interface classes; timestamped add/remove into `Recorder`; targeted re-probe for identity; poll tightens to 1 s for 60 s after a notification
- [ ] 1.3 `Recorder` dedup/order rules + tests
- [ ] 1.4 `EtwListener.cs` (opt-in): USBHUB3/USBXHCI/UCX → `linkDown`/`linkUp`/`portError` with `source: kernel`; `analysis.capabilities.linkEvents` reflects kernel/notification/poll/unavailable; never a coverage reason
- [ ] 1.5 Dashboard `incidents.ts`: `portError` as root; kernel > poll ranking; label source
- [ ] 1.6 Integration test: scripted <5 s reset+recover → incident with ordered events and root
- [ ] 1.7 Manual validation under Teams load (camera + mic + Ethernet + share) with a brief dock reset; record expected vs observed ordering — issue #37
