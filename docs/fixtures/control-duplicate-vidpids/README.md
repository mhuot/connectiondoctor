# control-duplicate-vidpids (constructed)

Two MX Verticals, same VID:PID, different node ids and different `unitKey`s.

VID:PID identifies a *model*. Any logic that treats it as a unit identity will
merge these two into one device that teleports, and will do the same to two
identical docks or two identical hubs — a common desk. The `unitKey`s differ
because the serials differ, which is exactly what that field is for.

## What this case executes today

**Duplicate VID:PID with distinct node ids.** That is the whole of it right
now. Incident stitching correlates on node id, and the TypeScript parser does
not model `unitKey` at all (it is still `opt, proposed`, issue #27), so
changing or deleting both unit keys would leave every executable assertion
unchanged except the format check on the raw JSON. Calling this a `unitKey`
disambiguation case would be a claim the code does not make.

The unit keys are here because the fixture should be what a real collector
emits, and because the case becomes the `unitKey` test the moment a consumer
reads the field — `contract-conformance` 1.1c and the identity work in #27/#62.
When that lands, this file gets an assertion that the two scoped identities
stay distinct through the consumer, and this section says so instead.
