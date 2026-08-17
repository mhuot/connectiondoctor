# fault-hub-branch-lost (constructed from the Surface Laptop 7 case)

The original Windows case: the LG UltraWide kept working while its built-in hub
and everything behind it stopped enumerating. Cold power-cycling the monitor
fixed it.

What makes it a fault rather than `control-sleep-wake` or `control-kvm-switch`:
nothing came back, and the display on the same dock never went away. An engine
must attribute the loss to the shared parent and say the individual devices are
innocent.
