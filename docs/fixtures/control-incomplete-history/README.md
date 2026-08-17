# control-incomplete-history (constructed)

The recorder started 30 minutes into a 6-hour window, and the log was trimmed.

The expected findings list is empty, and that emptiness means *nothing*: with
`coverage.complete: false` a consumer must say unknown. This is the fixture
that fails if someone ever renders an empty `findings` array as a clean bill of
health.
