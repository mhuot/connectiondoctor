# control-duplicate-vidpids (constructed)

Two MX Verticals, same VID:PID, different `unitKey`.

VID:PID identifies a *model*. Any logic that treats it as a unit identity will
merge these two into one device that teleports, and will do the same to two
identical docks or two identical hubs — a common desk. The `unitKey`s differ
because the serials differ, which is exactly what that field is for.
