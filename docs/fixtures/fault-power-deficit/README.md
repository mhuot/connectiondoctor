# fault-power-deficit (constructed from the original investigation)

A 68 W-rated adapter against demand it cannot meet: the battery quietly makes
up the difference while Windows reports AC power. This is the shape of the
fault that started the project — when demand exceeds supply, USB-C power
delivery renegotiates, the renegotiation resets the port, and the port reset
takes down everything behind it.

Contrast with `control-shallow-deficit`: same signal, an order of magnitude
smaller and momentary.

The envelope vouches for its window (`coverage.complete: true`), which is what
lets a consumer say the two minutes between `deficitStart` and `deficitEnd`
were two continuous minutes. Over an incomplete window the same two events
prove only their own endpoints — an end that went unrecorded and a later
restart look identical from the outside — so a consumer keeps both transitions
and makes no claim about the span between them. That distinction is exercised
directly in `dashboard/src/domain/conformance.test.ts`.
