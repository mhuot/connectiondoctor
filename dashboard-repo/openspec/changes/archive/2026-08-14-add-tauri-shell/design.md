# Design
Tauri v2, vanilla template (no framework bindings needed — it only hosts the
built assets). devUrl → Vite dev server; frontendDist → ../dist. No custom Rust
beyond the generated main. CSP left default-permissive for http: fetches to
collector endpoints; revisit when remote origins matter.
