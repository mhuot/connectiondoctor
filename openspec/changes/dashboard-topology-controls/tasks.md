# Tasks: dashboard-topology-controls
- [x] 1.1 `docs/schema-v1.md`: `nodes[].builtIn?` (producer classification; view decides)
- [x] 1.2 Producers: Windows sets `builtIn = !DeviceFilters.IsExternalDevice` on every device node; macOS marks Apple (05AC) internal keyboard/trackpad/FaceTime/ALS/Touch Bar/iBridge — producer tests follow with the conformance fixtures
- [x] 1.3 Dashboard: mode radio → Physical / All device nodes; feedback chip with folded/surfaced counts — issue #42
- [x] 1.4 Dashboard: Include built-in devices toggle (default off, persisted), "N built-in hidden" chip, inspector shows `builtIn` — issue #43
- [x] 1.5 Tests: chip text changes with mode independent of scroll (pure function over stats); Surface-class built-in filter hides panel/touch/HID, keeps root hub + LG + TB4 dock; unknown never hidden; badge accounting equals stats (5 tests)
- [ ] 1.6 READMEs: Windows README's stale "Include built-in devices" sentence becomes true again
