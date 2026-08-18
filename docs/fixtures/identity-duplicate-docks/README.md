# identity-duplicate-docks (constructed)

The same dock model — one VID:PID, and the same serial reported to each
collector — attached to two different endpoints.

The two `unitKey`s **differ**, and that is correct rather than a defect. The key
is `HMAC("USB|<VID:PID>|<serial>", installationKey)` where the installation key
is a random secret that never leaves the machine that generated it, so two
installations cannot produce the same key for the same hardware. That is what
stops a shared bundle from correlating one person's dock with another's.

It is also the limit, and stating it is the point of this fixture: because the
keys differ by construction, they are **not** evidence about whether one
physical dock moved between the two machines. A consumer that treated
`unitKey` as globally unique would answer "two different docks" here with
confidence it has not earned — the honest answer is that these keys are
meaningful only beside their own host. Cross-endpoint unit correlation needs a
tenant-scoped key and belongs to the fleet-integration milestone, which is
parked.

## Scope

Executed here: the two endpoints stay separate. **Asserted, not executed**: that
the keys never join across hosts — nothing in this repo correlates units
between endpoints, deliberately, so no code path exists to prove it wrong.
That gap closes with fleet work, not with 1.3/1.4.
