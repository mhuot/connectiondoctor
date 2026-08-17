# control-normal-unplug (constructed)

Someone unplugged a mouse, used it elsewhere, plugged it back twelve minutes
later. Nothing failed.

The trap: a single `deviceRemoved` is indistinguishable from the first event of
a real fault *at that instant* — the difference is that nothing else follows and
nothing shares a parent. An engine that warns here will warn every time anyone
moves a mouse between machines, and will be ignored when it matters.
