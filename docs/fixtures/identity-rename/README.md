# identity-rename (constructed)

One machine, renamed from `mini` to `mac-mini-office` between two samples six
hours apart. Both documents carry the same `host.id`.

A consumer must show **one endpoint with one history**. Keying on the hostname
splits this machine in two at the moment someone renames it, and the half with
the older name quietly stops receiving updates — a failure that looks like a
machine going offline rather than like a bug.

The dock keeps its `unitKey` across the rename, because the key is an HMAC
under the installation's secret and has nothing to do with what the machine is
called.

## Scope

This is a **consumer** case. It is about what the dashboard does with two
documents; a producer only ever emits one at a time and never sees a rename as
an event. So there is nothing here for the Swift or C# engines to reproduce,
and `contract-conformance` 1.3/1.4 parity does not apply to it.
