# LumioGameRuntime Workflow Requirements Snapshot

This is a read-only snapshot of the `LumioGameRuntime` requirement room in
Workflow. It records the API state inspected on 2026-08-28 and is not a second
source of truth for requirement status.

## Room

- Project: `LumioGamesEngine` (`lumiogamesengine.workflow.games`)
- Room: `RM-00005` / `LumioGameRuntime`
- Room UUID: `01a04225-7526-70be-8950-32f83dd061fd`
- Overview phase: `needs_definition`
- Requirement count: 31 (`backlog=31`, `unstarted=0`, `started=0`, `completed=0`)
- Work items: 0 active, 0 closed
- Acceptance overview: 1 requirement missing acceptance; 244 acceptance items not passed
- Bugs: 0 unresolved, 0 unresolved blockers
- Card read completeness: all 31 requirement bodies, comments, attachments and acceptance-item lists were fetched; comments were empty; R-00049 has two source attachments and the other cards have none. The two attachment payloads matched the checked-in plan files by SHA-256.

## Requirement Index

All cards are owned by the current Workflow user and are still `backlog`. The
acceptance count is the number of online acceptance items; all listed items are
`not_started` unless stated otherwise.

| Key | Module | Wave | Plan slug | Status | Acceptance |
| --- | --- | --- | --- | --- | ---: |
| [R-00049](https://lumiogamesengine.workflow.games/requirements/01a0438b-1512-76f6-81ad-def2f09a56b0) | repository | source | Foundation and first runnable slice | backlog | 0 |
| [R-00112](https://lumiogamesengine.workflow.games/requirements/01a043a5-1a75-7d1f-89e4-20371ed826d4) | repository | 1 | repo-dotnet-baseline | backlog | 7 |
| [R-00127](https://lumiogamesengine.workflow.games/requirements/01a043a7-3940-71da-970d-f34879b26eb9) | repository | 2 | repo-supply-chain-policy | backlog | 8 |
| [R-00131](https://lumiogamesengine.workflow.games/requirements/01a043a7-f4b8-7062-9731-d9a619e18017) | repository | 3 | repo-generated-contract-boundary | backlog | 8 |
| [R-00133](https://lumiogamesengine.workflow.games/requirements/01a043a8-8098-78ba-b764-367b60d6befc) | observability | 4 | obs-event-ports-and-context | backlog | 7 |
| [R-00138](https://lumiogamesengine.workflow.games/requirements/01a043a9-5c76-76db-bb6f-40654092f4f8) | observability | 5 | obs-foundation-routing-and-failure | backlog | 9 |
| [R-00139](https://lumiogamesengine.workflow.games/requirements/01a043a9-c17a-7b46-93e4-335fe0c41e24) | config | 5 | cfg-validation-and-six-layer-merge | backlog | 8 |
| [R-00140](https://lumiogamesengine.workflow.games/requirements/01a043aa-1a62-7e78-8cd0-5834deba41ee) | config | 6 | cfg-snapshot-and-tick-activation | backlog | 8 |
| [R-00141](https://lumiogamesengine.workflow.games/requirements/01a043aa-84a7-729e-b270-818802b3a3ff) | persistence | 6 | persist-foundation-canonical-codec | backlog | 8 |
| [R-00149](https://lumiogamesengine.workflow.games/requirements/01a043ae-821e-7812-9283-28fffb6b649a) | ecs | 7 | ecs-identity-and-storage-adapter | backlog | 9 |
| [R-00150](https://lumiogamesengine.workflow.games/requirements/01a043ae-dda4-716e-b045-eb4ae9b2d732) | ecs | 8 | ecs-query-views-and-changes | backlog | 8 |
| [R-00152](https://lumiogamesengine.workflow.games/requirements/01a043af-b7c9-714d-8c83-7297e839d7a9) | ecs | 9 | ecs-lifecycle-owner-thread-and-fail-stop | backlog | 8 |
| [R-00154](https://lumiogamesengine.workflow.games/requirements/01a043b0-12e1-77c3-a342-62a9a131af71) | command | 10 | cmd-buffer-deferred-and-stable-merge | backlog | 8 |
| [R-00157](https://lumiogamesengine.workflow.games/requirements/01a043b0-cb59-772d-af68-1a8b23335745) | command | 11 | cmd-preflight-prepared-and-apply | backlog | 8 |
| [R-00159](https://lumiogamesengine.workflow.games/requirements/01a043b1-3840-7dca-948c-669e3baca22d) | gas | 11 | gas-foundation-type-handle-context | backlog | 8 |
| [R-00162](https://lumiogamesengine.workflow.games/requirements/01a043b1-8c13-74e5-81d0-c2101a53ad5e) | command | 12 | cmd-budget-durable-evidence-and-conflicts | backlog | 8 |
| [R-00164](https://lumiogamesengine.workflow.games/requirements/01a043b1-c252-7ca4-942a-08837b4b1eb4) | coordination | 12 | coord-revision-and-txn-state | backlog | 9 |
| [R-00167](https://lumiogamesengine.workflow.games/requirements/01a043b2-01d6-71e4-8546-9d1f12fe7e13) | coordination | 13 | coord-prepare-and-reservation | backlog | 8 |
| [R-00172](https://lumiogamesengine.workflow.games/requirements/01a043b2-83b9-76d5-9aa8-e6f588e0ec00) | replication | 13 | repl-foundation-mapping-and-identity | backlog | 8 |
| [R-00174](https://lumiogamesengine.workflow.games/requirements/01a043b2-d5fc-7eed-901e-974148c5b46c) | coordination | 14 | coord-commit-intent-apply-and-recovery | backlog | 9 |
| [R-00176](https://lumiogamesengine.workflow.games/requirements/01a043b4-3383-7629-a344-9d34737aaf1a) | coordination | 14 | coord-snapshot-cut | backlog | 6 |
| [R-00178](https://lumiogamesengine.workflow.games/requirements/01a043b4-e619-75e1-ba9d-b09feae181f3) | simulation | 15 | sim-session-and-single-run-tick | backlog | 8 |
| [R-00181](https://lumiogamesengine.workflow.games/requirements/01a043b5-4b28-7f25-8ddf-577793c46b5c) | testing | 15 | test-reference-voxel-authority-port | backlog | 7 |
| [R-00184](https://lumiogamesengine.workflow.games/requirements/01a043b5-8553-7c74-8764-4b28263ab38e) | simulation | 16 | sim-exact-13-phase-graph | backlog | 7 |
| [R-00187](https://lumiogamesengine.workflow.games/requirements/01a043b6-2914-7c30-a8b1-af7c24b11d81) | simulation | 17 | sim-processor-plan-validator | backlog | 8 |
| [R-00189](https://lumiogamesengine.workflow.games/requirements/01a043b6-6f36-7bee-9abd-0d6e1db61c2b) | simulation | 17 | sim-ingress-canonicalization-and-native-barrier | backlog | 8 |
| [R-00191](https://lumiogamesengine.workflow.games/requirements/01a043b6-c697-70f2-9086-d73bfdc266a3) | simulation | 17 | sim-determinism-context-and-state-hash | backlog | 8 |
| [R-00192](https://lumiogamesengine.workflow.games/requirements/01a043b7-036a-7ef2-a889-34900d9c1c05) | simulation | 18 | sim-tick-runner-fail-stop-and-result | backlog | 11 |
| [R-00195](https://lumiogamesengine.workflow.games/requirements/01a043b7-6301-7574-ad98-78c7dc77c688) | testing | 19 | test-reference-host-foundation-slice | backlog | 9 |
| [R-00197](https://lumiogamesengine.workflow.games/requirements/01a043b7-aed1-71f4-a761-fbfb0524b42d) | testing | 20 | test-replay-and-first-difference | backlog | 8 |
| [R-00199](https://lumiogamesengine.workflow.games/requirements/01a043b7-fc53-7927-b195-b4bcd2b7e02d) | repository | 21 | repo-solution-graph-and-foundation-gate | backlog | 10 |

Online links use the UUID as the canonical route:
`https://lumiogamesengine.workflow.games/requirements/<uuid>`.

## Dependency Waves

The following is the dependency graph encoded in the card bodies. R-00049 is
the source record and R-00131 is the generated-contract hard gate. A card is
not considered executable merely because the room overview labels it
`start_ready`; its own prerequisite section and acceptance evidence remain
authoritative.

- Wave 1: R-00112 (repository SDK baseline), no online prerequisite.
- Wave 2: R-00127, after R-00112.
- Wave 3: R-00131, after R-00112 and R-00127; consumes the architecture-source generator.
- Wave 4: R-00133, after R-00131.
- Wave 5: R-00138 and R-00139, after R-00133 and R-00131; their file sets do not overlap.
- Wave 6: R-00140 after R-00139; R-00141 after R-00133 and R-00139.
- Waves 7-9: R-00149 -> R-00150 -> R-00152 (ECS identity, views, lifecycle).
- Waves 10-12: R-00154 -> R-00157; R-00159 is parallel with R-00157; R-00162 and R-00164 follow their respective command/coordination prerequisites.
- Waves 13-14: R-00167 and R-00172 are parallel; R-00174 and R-00176 follow their coordination/replication prerequisites.
- Waves 15-18: R-00178 and R-00181 are the first integration wave; R-00184 -> R-00187/R-00189/R-00191 -> R-00192.
- Waves 19-21: R-00195 -> R-00197, with R-00199 as the final solution/DAG/architecture gate.

The bodies of R-00133 through R-00199 also cite R-00049 as their source
requirement. R-00049 currently has no acceptance items, so the room overview
reports one missing-acceptance requirement.

## File Ownership

The cards declare disjoint implementation roots for parallel work:

- Repository: root SDK/build policy, dependency policy, generated-contract wrapper, solution and architecture gate (`R-00112`, `R-00127`, `R-00131`, `R-00199`).
- Observability: `modules/observability` (`R-00133`, `R-00138`).
- Config: `modules/config` (`R-00139`, `R-00140`).
- Persistence: `modules/persistence` (`R-00141`).
- ECS: `modules/ecs` (`R-00149`, `R-00150`, `R-00152`).
- Command: `modules/command` (`R-00154`, `R-00157`, `R-00162`).
- GAS: `modules/gas` (`R-00159`).
- Coordination: `modules/coordination` (`R-00164`, `R-00167`, `R-00174`, `R-00176`).
- Replication: `modules/replication` (`R-00172`).
- Simulation: `modules/simulation` (`R-00178`, `R-00184`, `R-00187`, `R-00189`, `R-00191`, `R-00192`).
- Testing: `modules/testing` (`R-00181`, `R-00195`, `R-00197`); R-00181 creates the project shell and R-00195 extends it.

Shared hot spots are explicitly reserved for R-00199 (solution/root graph and
final gate) and for R-00181/R-00195 (testing project shell). They must not be
edited by parallel module agents.

## Current Gates

1. **Architecture source gate (R-00131):** the local architecture checkout now
   has W1 landed at `HEAD 5f0682248f4baffa5847c2cee654d511640f8bef`. Its
   `packages/index.json` publishes 12 V1.4 artifacts (six Rust/C# families),
   baseline `LGE-V1.4-2026-08-27`, 12 state machines, and only `D-009`/`D-011`
   blocked. Descriptor and output-hash checks match for all 12 artifacts, and
   `C:\Users\g923\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe
   tools/lumio_contract.py validate --json` reports `validated=160` and
   `failures=0`. The architecture branch is still three commits ahead of
   `origin/main`, so this is locally landed evidence, not externally published
   evidence; the Runtime wrapper must consume the checked-out packages only
   after that publication boundary is confirmed.
2. **Build environment gate (R-00112 through R-00199):** the cards require
   exact `.NET SDK 10.0.11`, but official release metadata for `10.0.11` lists
   SDKs `10.0.400`, `10.0.303`, and `10.0.111`; no SDK `10.0.11` exists. The
   official user-local installer was run for the closest valid SDK `10.0.111`
   at `C:\Users\g923\.dotnet`; it reports `dotnet --version` as `10.0.111`
   and includes runtime `10.0.11`. This is usable tooling, but it cannot be
   reported as the exact SDK version demanded by the cards. The embedded
   Unreal installation exposes SDK `10.0.203`, which remains unused. `python3`
   is unavailable on PATH, although the absolute Python runtime above can
   execute the architecture validator. T01 has now created the six root
   baseline files (`global.json`, `Directory.Build.props`,
   `Directory.Build.targets`, `.editorconfig`, and the Bash/PowerShell SDK
    verification scripts). T02 has added the central package/version policy,
    NuGet source configuration, license/scope policy, dependency verifier, SBOM
    wrappers, and third-party notices; both shells pass the empty-project gate
    (`projects=0`), while the verifier rejects a floating package fixture with
    `FLOATING_VERSION_FORBIDDEN` and exit `31`. T03 has now added the generated
    contract project, six checked-in C# artifacts, manifest, regeneration/drift
    gates, and focused tests under `src/` and `tests/`. T04 has added the
    observability production/test projects, immutable Event/Metric/Trace ports,
    lifecycle facade, generated-error validation, and concurrent producer
    sequence coverage under `modules/observability`. With the installed SDK
    `10.0.111` in an isolated temporary project graph, all production targets
    (`net10.0` and `netstandard2.1`) build with 0 warnings/errors; the T04
    direct test run reports 4 passed and 0 failed. Locked restore, dependency
    policy (`projects=4`), SBOM (`packages=28`), generated-contract drift, and
    provider/channel public-surface checks pass. The repository root remains
     intentionally unbuildable through normal `dotnet` invocation while its
     exact `10.0.11` SDK pin is unresolved.
   T05 (`R-00138`) is implemented locally under `modules/observability`: the
   Diagnostic path uses an internal bounded `Channel` with item/byte budgets,
   explicit `DroppedBestEffort`/`QueueFull` accounting, and close semantics;
   durable evidence uses an independent bounded router with idempotency-key
   replay and explicit `Backpressured` results; Failure Bundle assembly checks
   artifact SHA-256 references and enforces the Snapshot/no-snapshot XOR,
   including bootstrap context before the first valid Snapshot. The focused
   observability T04/T05 suite reports `11 passed / 0 failed` under the
   installed SDK `10.0.111` (runtime `10.0.11`).
3. **Room definition gate (R-00049):** the room overview reports one missing
   acceptance definition for the source requirement. No Workflow state or
   acceptance item was changed during this audit.

## Dispatch Decision

Two parallel sub-agent audits were dispatched for the explicit request to
parallelize work: one checked the architecture/toolchain and SDK blockers, and
one derived the wave/file ownership boundaries. The architecture gate is now
locally satisfied. T01's baseline files and T02's supply-chain policy are
present, but T01's exact SDK check correctly fails (`SDK_MISMATCH`, exit `21`)
because the installed SDK is `10.0.111`; the generated-contract boundary and
T04 observability ports are locally implemented, with isolated build/test
evidence recorded above. Later module waves remain blocked until the card is
clarified or a valid SDK requirement is provided. No Workflow status transition,
new work item, comment, attachment, or cross-room operation was performed.
