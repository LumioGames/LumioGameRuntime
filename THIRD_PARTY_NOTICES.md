# Third-Party Notices

Runtime package versions are centrally pinned in `Directory.Packages.props`.
Restore must use a `packages.lock.json` file and CI must use locked mode. The
dependency verifier checks the package graph and reads SPDX license evidence
from each restored package's `.nuspec`; a missing or non-SPDX license is a
release-blocking `DEPENDENCY_LICENSE_UNKNOWN` result.

| Package | Version | SPDX license | Permitted use |
| --- | ---: | --- | --- |
| `xunit.v3` | `4.0.0` | Apache-2.0 | Test projects and `ReferenceHost` only |
| `Microsoft.Testing.Platform` | `2.3.3` | MIT | Test projects and `ReferenceHost` only |
| `coverlet.MTP` | `10.0.1` | MIT | Test projects only |
| `CsCheck` | `4.7.0` | MIT | Test projects only |
| `Friflo.Engine.ECS` | `3.6.0` | MIT | `Lumio.GameRuntime.Ecs.Adapters.Friflo` only |
| `MessagePack` | `3.1.8` | MIT | `Lumio.GameRuntime.Persistence` adapter only |

`Friflo.Engine.ECS` and `MessagePack` are replaceable adapter implementation
dependencies, not runtime contract or stable public API dependencies. SBOM
generation is a build/release concern and does not add an SBOM tool to any
production assembly.

GPL, AGPL, LGPL, and any other license that cannot be confirmed as an SPDX
expression require legal review or are rejected by the policy gate. This file
is a notice index; the verifier's report is the authoritative per-restore
license and package-hash evidence.
