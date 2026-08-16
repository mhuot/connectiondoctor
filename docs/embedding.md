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
- `Access-Control-Allow-Origin: *`, so a dashboard running against a Vite dev
  server can still read a collector on another port.

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
