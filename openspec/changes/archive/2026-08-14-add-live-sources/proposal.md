# Proposal: add-live-sources

## Why
Collectors now serve the contract over HTTP (TBDoctor `--serve`: GET /contract,
GET /events). The dashboard only loads files; the fleet use case — several
always-on machines, one dashboard — needs it to pull from collectors directly.

## What
- Extend `HttpSource` to fetch both `/contract` and `/events` from a base URL.
- UI: add a host by URL; refresh all HTTP-origin hosts on demand.
- HTTP-origin data is still labelled with its captured timestamp (a fetch is a
  snapshot, not a live stream — no fake freshness).

## Non-goals
Polling/streaming (a fetch button is honest until producers push), and
authentication (endpoints are opt-in LAN, read-only, per TBDoctor's design).

## Impact
Capability `contract-ingest` gains HTTP scenarios; `data/sources.ts`, App shell.
