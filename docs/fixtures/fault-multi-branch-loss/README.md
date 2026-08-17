# fault-multi-branch-loss (constructed from a recorded observation, issue #37)

A live `/contract` on a Windows ARM64 Surface dropped from **103 nodes to 47**
and stayed there across repeated samples for over six minutes. Ethernet failed
over to Wi-Fi and both external displays went dark, so something real happened
to a lot of devices at once.

This case is constructed, not recorded, and the label matters: the original
documents are held back because their instance IDs carry device-scoped
identifiers, and the shape here is reduced to eight nodes so the point is
legible. What is preserved exactly is the property that made the observation
worth keeping — **the losses span two branches of the host**.

## Why the answer is "root unknown"

Six devices disappear together. Four of them hang off the dock; two are
displays attached directly to the host. The only node that is an ancestor of
all six is therefore the host itself.

A host root is an ancestor of everything, so finding it there says nothing
about *why* the six went together — and rendering it as "all behind surface —
one upstream failure" would be a claim about a machine that is plainly still
running and still reporting. Naming the dock instead would be worse: it is the
obvious suspect, the simultaneity and the Ethernet failover both point at it,
and the topology still does not prove it. Two of the losses were never behind
it.

So the expected incident has **`sharedParent: null`**: one correlated
disappearance, cause not established. The grouping is real and worth showing;
the root is not ours to name. An engine that answered "the dock" would be right
here and wrong the first time two unrelated things fail in the same second,
which is the failure this corpus exists to prevent.

## What else is unknown, and why

The recorder was not running across the transition, so there is no `linkDown`
or `portError` to attribute anything to — the envelope says so
(`coverage.complete: false`, `recorder-started-inside-window`). That absence is
the subject of issue #37: a reset that completes between five-second polls
leaves no trace at all. Until sub-poll events land, the honest report of a
disappearance like this one names the fallout, the incomplete window, and no
cause.

The `deviceRemoved` events here are constructed to place the losses on the
timeline; the real observation had no event stream for them, which is the whole
point of #37.
