# Embedding the dashboard in a collector

The dashboard is the UI for both collectors — [ConnectionDoctor](../windows/README.md)
on Windows and [TBDoctor](../macos/README.md) on macOS. Each one
compiles this bundle into its own binary and serves it next to the Connection
Contract v1 endpoints, so a user downloads one file and opens one URL. Node is a
build-time dependency of whoever cuts the release, never of the user.

This document is the contract between the bundle and its hosts. Both producers
implement it, so the same build behaves identically on either OS.

## Producing the bundle

```sh
cd dashboard && npm ci && npm run build   # → dashboard/dist, ~235 KB across 5 files
```

`vite.config.ts` sets `base: './'` so every asset reference in `index.html` is
relative. Do not change that: absolute `/assets/...` URLs would hard-code the
bundle to being mounted at the server root and break any host that does not.

Staged output is build output. Neither collector commits it; each has a script
that builds this repo and copies `dist` into its embed directory.

## Routes a host must serve

| Route | Response |
|---|---|
| `GET /` | `index.html` |
| `GET /index.html` | same |
| `GET /assets/<file>` | the fingerprinted asset |
| `GET /favicon.svg`, `GET /icons.svg` | those files |
| `GET /contract` | Connection Contract v1 envelope |
| `GET /events` | v1 events JSONL |
| `POST /baseline` *(proposed, `contract-findings-incidents`)* | Capture, or replace, the known-good baseline from the current state. The first state-changing route, so it has its own rules — see "Mutations" below |
| anything else | **404** |

Unknown paths must 404 rather than fall back to `index.html`. The app has no
client-side router, so a catch-all would turn a mistyped asset URL into an HTTP
200 serving HTML — which surfaces as an unreadable MIME error in the console
instead of an honest missing-file error.

## Headers

- `Content-Type` per extension; `.js` must be `text/javascript`, `.css`
  `text/css`, `.svg` `image/svg+xml`. A wrong type on the module script blocks
  execution outright under strict MIME checking.
- `Cache-Control: public, max-age=31536000, immutable` for `assets/*`, whose
  names are content-hashed.
- `Cache-Control: no-cache` for `index.html`. Without it an updated binary keeps
  serving the previous bundle's asset names, and the app fails to boot.
- `Server: connectiondoctor/<version>` on **every** response (assets, `/contract`,
  404s). This is the product identity the `ui` verb and the resident process's
  "Open dashboard…" check before reusing a port that already answers: any
  other service on 8787 returning a 2xx must not be mistaken for us. `<version>`
  is the collector's `version` (e.g. `connectiondoctor/0.1.0`, `tbdoctor/0.1.0`
  — the product token names the binary, so a fleet can tell them apart).
- `Access-Control-Allow-Origin: *`, so a dashboard running against a Vite dev
  server can still read a collector on another port.

## Mutations

`POST /baseline` (and any future state-changing route) is **not** covered by
the read-only rules above. "Loopback only" is necessary but not sufficient: a
malicious page open in the user's browser can send a simple cross-origin POST
to `http://localhost:8787` — CORS decides whether it can *read* the response,
not whether the request is sent. So a mutation:

- is served **only when bound to loopback**; a LAN-bound server answers `403`
  with `{"error":"read-only-binding"}`;
- **requires the request to be same-origin**: the `Origin` header must equal
  the origin the bundle is served from (`http://localhost:<port>` or
  `http://127.0.0.1:<port>`); missing, `null` or any other origin (a Vite dev
  server, a LAN address, another site) → `403 {"error":"cross-origin"}`;
- **requires the custom header `X-ConnectionDoctor-Request: 1`**, which makes
  the request non-simple so browsers preflight it; the server answers
  preflights for mutation routes **without** `Access-Control-Allow-Origin`, so
  a cross-origin caller is blocked before the POST is ever sent (belt and
  braces with the `Origin` check);
- **never returns `Access-Control-Allow-Origin: *`** on a mutation response —
  no CORS headers at all;
- **replace is conditional**: `?replace=1` must carry `If-Match: "<capturedAt of
  the baseline the client saw>"` — **exactly one strong ETag**: a quoted
  timestamp. Unquoted, weak (`W/"…"`), `*` or multiple values are refused. A
  mismatch (another tab already replaced it) →
  `409 {"error":"stale","current":{"capturedAt":"…"}}`;
- **the `Origin` must equal the scheme and authority the request arrived on**;
  `http://localhost:8787` and `http://127.0.0.1:8787` are different browser
  origins and are not interchangeable;
- **read, decide and write are one transaction** under a named cross-process
  lock, so the CLI's `baseline save` and this route cannot interleave; the
  write is atomic (temp file, then replace). It is **fail-closed**: an
  existing baseline that cannot be read is `500 {"error":"baseline-unreadable"}`,
  never treated as absent — treating it as absent would let a replace bypass
  the check and discard a known-good state. A lock that cannot be taken in
  time is `503 {"error":"busy"}`;
- returns structured metadata: `201 {"baseline":{"capturedAt":"…","nodes":N},"replaced":false}` /
  `200 …"replaced":true`; capture when a baseline already exists and
  `replace` is not set → `409 {"error":"exists","current":{…}}`.

Tests both hosts must pass: same-origin capture succeeds; a cross-origin
simple POST is refused and never mutates; a POST to a LAN-bound server is
refused; two tabs — the second replace with the old `If-Match` gets 409.

## Path safety

Reject any request path containing `..` or a drive/scheme colon, after URL
decoding. The bundle is a fixed set of embedded resources; a request that does
not match one by exact name is a 404, never a filesystem lookup.

## Self-connection

On load the app calls `loadHttp(window.location.origin)` once. When a collector
is serving the bundle, that origin answers `/contract` and the machine appears
with no interaction — opening the URL is the entire setup. When the origin does
not answer (Vite dev, a static host), the failure is swallowed and the normal
empty state appears.

This is why the routes above live on the same origin as the UI. A host that
serves the bundle from a different origin than the contract endpoints still
works, but the user has to add the collector by address by hand.
