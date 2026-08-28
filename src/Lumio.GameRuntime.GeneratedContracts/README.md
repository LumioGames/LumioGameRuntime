# Generated Contracts

This project is a read-only boundary around the C# artifacts published by
`LumioGameEngineArchitecture`. It has no Runtime module or third-party package
references. Sources under `Generated/` and its manifest are generated; do not
edit them by hand.

Set `LUMIO_ARCHITECTURE_ROOT` to an architecture checkout and run:

```text
bash eng/generate-contracts.sh
powershell -File eng/generate-contracts.ps1
```

The scripts invoke `tools/lumio_contract.py generate --out <temporary package
directory>`, then copy the six C# artifacts and write a manifest containing the
architecture commit, baseline, schema epoch, compiler/input hashes, registry
hashes, and artifact output hashes. `verify-generated-contracts.*` regenerates
into a temporary directory and fails with exit code `32` on drift.
