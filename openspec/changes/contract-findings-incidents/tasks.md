# Tasks: contract-findings-incidents
- [ ] 1.1 `docs/schema-v1.md`: envelope `findings[]`, `incidents[]`, `analysis{}`; Finding/Incident tightened; state that consumers may derive incidents when absent
- [ ] 1.2 macOS: `Severity` string raw values; envelope emits findings/incidents/analysis from existing engine; `devicesLost` with vidPid; mW
- [ ] 1.3 Windows: `Finding.Evidence`; deficit engine port; envelope emits findings/incidents/analysis
- [ ] 1.4 Dashboard: types + parse (optional, tolerant); tests incl. fixture with findings
- [ ] 1.5 Dashboard: `FindingsView` panel; Timeline prefers producer incidents and labels source
- [ ] 1.5b Dashboard: per-host contact tracking + state chip (live/stale/offline/envelope-only/history-incomplete); surface skippedLines and fetch errors per host; "none" only on a complete window; tests for online-envelope-failed-events, corrupt JSONL, stale retained data, recovery on refresh — issue #29
- [ ] 1.6 Regenerate `surface-chain.v1.json` and add a findings-bearing fixture from a real recording
- [ ] 1.7 READMEs: dashboard "What works today" gains findings; TBDoctor's screenshot of native Timeline replaced later by the dashboard's
