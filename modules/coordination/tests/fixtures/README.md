# Coordination Fixtures

The `valid` and `invalid` directories contain durable replay inputs for the
duplicate, timeout, lost-result, partial-commit, and crash-boundary paths.
Every V2 artifact carries the complete framed transaction identity and revision
digests. Recovery cases also carry each journal stage's canonical payload bytes,
links, sequence, previous hash, payload hash, checksum, and exact result-evidence
fields. `DurableFailureFixtureTests` parses those values directly, recomputes all
digests, appends the supplied records through the Runtime journal, and drives the
corresponding state machine. It does not synthesize terminal markers or evidence
rows from abbreviated fixture metadata.
