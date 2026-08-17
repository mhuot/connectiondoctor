# control-shallow-deficit (constructed)

The near-miss for `fault-power-deficit`, and the reason the two thresholds are
different numbers: the event log records any deficit past -2 W so the timeline
is honest, while a *finding* needs 10 W sustained across at least two samples.

Without this control, someone lowering the finding threshold to 'catch more'
would make the tool cry wolf on every laptop sitting at full charge.
