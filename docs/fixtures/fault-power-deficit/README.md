# fault-power-deficit (constructed from the original investigation)

A 68 W-rated adapter against demand it cannot meet: the battery quietly makes
up the difference while Windows reports AC power. This is the shape of the
fault that started the project — when demand exceeds supply, USB-C power
delivery renegotiates, the renegotiation resets the port, and the port reset
takes down everything behind it.

Contrast with `control-shallow-deficit`: same signal, an order of magnitude
smaller and momentary.
