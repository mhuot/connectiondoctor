# Design: dashboard-topology-controls

## Why a contract flag and not a view heuristic
The Windows collector already classifies built-ins (`DeviceFilters`), and the
old WinForms toggle used it. Re-deriving "built-in" from names in React would
be a third copy of a rule that is drifting between the producers already
(see `contract-conformance`). One optional boolean per node, set by the side
that knows, keeps the rule where the evidence is.

## Default off, and honest about it
External-focused is what a dock-fault tool is for. When the filter hides
nodes, the chip says how many ("12 built-in hidden"), so nothing disappears
silently — the same rule as `+N internal`.

## Feedback without scrolling
The mode chip is computed from the collapse result the view already has
(`collapse()` returns the folded count per container). Switching modes
changes the chip text and count immediately, independent of scroll.

## Naming
"Physical" stays. "+ logical" becomes "All device nodes": it is exactly the
exported node list, no more.
