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

## Generation always anchors to a committed object

The architecture checkout's **working tree is never read**. The scripts resolve
a commit — `LUMIO_ARCHITECTURE_COMMIT` (an exact SHA) takes precedence over
`LUMIO_ARCHITECTURE_REF` (default `origin/main`) — export that commit read-only
with `git archive`, and run `tools/lumio_contract.py` from the exported
snapshot. A ref that does not resolve is a hard failure
(`ARCHITECTURE_COMMIT_MISSING`, exit `31`); there is no fallback to the working
tree. That checkout is edited concurrently, so anything generated from it would
depend on when the command happened to run.

The export is pinned with `-c core.autocrlf=false -c core.eol=lf
-c core.attributesfile=<empty>` and limited to the `tools schemas ids fixtures`
pathspec. None of that is optional. The architecture source sets `* text=auto`,
so without the overrides `git archive` applies the *caller's* line-ending
configuration and the same commit yields different bytes — and therefore
different registry hashes — on Windows than on Linux or macOS. Attributes beat
configuration, so a global `~/.gitattributes` carrying `* eol=crlf` overrides
the first two flags; the third one closes that. The pathspec drops 42 symlinks
that stock Windows `tar` cannot create outside Developer Mode, and produces
output identical to a full-tree export.

Two machine-local inputs remain **unclosed**: `$GIT_DIR/info/attributes`, which
has no configuration switch at all, and a `filter` smudge command, which
`git archive` applies and which is defined by local configuration (no `filter`
is declared today). The verdict is therefore a function of
`(commit, attribute stack)`, not of the commit alone. Closing that completely
means hashing blobs directly with `git cat-file blob <commit>:<path>` instead of
hashing materialized files.

The manifest records that exact commit alongside the baseline, schema epoch,
compiler and input hashes, registry hashes, and artifact output hashes.

## Verification runs two checks of different kinds

`verify-generated-contracts.*` regenerates from the commit **recorded in the
committed manifest** and compares file by file:

- **Artifact integrity** is the hard gate and owns the exit code. Drift exits
  `32` and names the differing paths. It proves the generated files were not
  hand-edited and that the manifest's provenance is real.
- **Upstream divergence** is reported only and never changes the exit code. It
  compares the pinned commit against the upstream ref using git facts alone —
  no second generator run — and distinguishes a contract-surface move
  (`schemas/`, `ids/`, `fixtures/`) from a generator-only move (`tools/`).

Moving to a newer architecture release is therefore an explicit act: pass the
new commit and regenerate. See
[`.spec/decisions/0002-generated-contract-gate-anchors-committed-objects.md`](../../.spec/decisions/0002-generated-contract-gate-anchors-committed-objects.md).
