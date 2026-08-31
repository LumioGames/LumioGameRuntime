# Command Fixtures

These fixtures document the command-buffer evidence boundary used by the
focused tests. Command payloads remain runtime-internal until the architecture
contract publishes a command schema; cross-world results use the generated
transaction and command-log contracts.

Each fixture records the input buffer scopes, canonical merged order, preflight
outcome, and apply receipt or first failure. Tests must keep ordering tied to
`Phase + ProcessorId + LocalSequence`, never arrival time or object identity.
