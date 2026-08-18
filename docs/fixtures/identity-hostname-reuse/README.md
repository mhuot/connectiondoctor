# identity-hostname-reuse (constructed)

Two different machines both called `surface` — an ordinary outcome of an imaging
process that names by model, and the reason `host.id` exists at all.

They must stay **two endpoints**. Merging them produces a single host whose
topology flickers between two real machines on every refresh, which is worse
than either machine being missing: the display is confidently wrong rather than
obviously incomplete.

The third element is the harder half. `surface.events.jsonl` names a host by
convention and nothing more, and two candidates answer to that name. Attaching
it to whichever document arrived first would put one machine's history onto
another and report a device as lost when it never left. So the stream attaches
to **neither**, and is kept as an unattributed entry carrying the reason —
because silently discarding it is the other way to get this wrong, and the
quieter one.

## Scope

A **consumer** case, like the other two identity fixtures: it is about what
happens when two documents meet, which is a situation no producer is ever in.
`contract-conformance` 1.3/1.4 parity does not apply.
