# LumioGameRuntime 模块化架构审查报告

- **审查对象**：LumioGameRuntime 当前模块化设计
- **架构基线**：`LGE-V1.0-2026-08-27`
- **审查性质**：只读、对抗式、证据驱动
- **审查重点**：模块边界、职责、依赖、状态所有权、协议完整性和实现准备度
- **总体裁决**：**退回，Architecture Gate 未通过**

---

## 1. Findings

### 1.1 Findings 汇总

| 严重级别 | 编号 | 结论 |
|---|---|---|
| P0 | RT-AR-001 | `EcsCommandBufferCommit` 仍可能在 `VoxelCommit` 之后发生业务拒绝，破坏 CrossWorldTxn 原子性 |
| P0 | RT-AR-002 | 普通 Component 字段写入缺少 Tick 级失败原子性与可见性契约 |
| P1 | RT-AR-003 | `SimulationSession` 的 Revision、Txn、Coordinator 状态所有权在根 README 与模块文档中冲突 |
| P1 | RT-AR-004 | `Prepared -> Indeterminate` 状态图与 Contract Validator、恢复语义不一致 |
| P1 | RT-AR-005 | Replication 声明原子应用 Voxel Overlay，但依赖 DAG 和模块依赖中没有 Voxel Replica Port |
| P1 | RT-AR-006 | Persistence 的 Snapshot Provider 集合遗漏 Voxel 状态参与者 |
| P1 | RT-AR-007 | Replication Envelope 无法结构化表达并验证 V1 所需的 Revision、Mapping、认证及资源约束 |
| P1 | RT-AR-008 | Replication `messageType` 枚举与公共 ID Registry 不一致 |
| P1 | RT-AR-009 | Entity Identity 的 `namespace` 不是必填字段，身份域隔离可被绕过 |
| P1 | RT-AR-010 | ProcessorDescriptor 语义校验与 CommandBuffer/Processor 执行模型矛盾 |
| P1 | RT-AR-011 | GAS 通用 Ability/Effect 状态机被模糊地委托给 Game Content |
| P1 | RT-AR-012 | TxnJournal、CommandLog、WAL 缺少独立、可恢复、可版本化的记录契约 |
| P1 | RT-AR-013 | Config P0 Schema 不验证列类型、范围和引用，无法支撑 typed table 承诺 |
| P1 | RT-AR-014 | Hot Reload 缺少双 Scope Staging、Tick Barrier 原子切换和明确回滚点 |
| P2 | RT-AR-015 | 当前 DAG 同时表示编译依赖和逻辑依赖，但遗漏多个实际依赖边 |
| P2 | RT-AR-016 | 队列和资源限制大多停留在定性描述，尚不能直接实现一致的背压策略 |
| P2 | RT-AR-017 | Tombstone、Baseline、History、Prediction 窗口之间没有可验证的保留下界 |
| P2 | RT-AR-018 | Config 编译器在 Runtime、Game 和 Toolchain 之间的所有权不清晰 |
| P2 | RT-AR-019 | FailureBundle 强制要求 `snapshotId`，无法表示首次有效 Snapshot 之前的故障 |
| P3 | RT-AR-020 | 根 README 对日志队列的概括容易把 Diagnostic 与耐久 Journal 误解为同一路径 |

---

### P0 — RT-AR-001：ECS Apply 可能在 Voxel Commit 之后发生业务拒绝

- **类型**：事实性契约冲突。
- **位置**：
  - Runtime `modules/command/README.md:61-64`
  - Runtime `modules/coordination/README.md:69-75`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:256-261`
  - Architecture `fixtures/index.json:9`
- **关联架构源**：§6.2 CrossWorldTxnV1、ADR-003、`cross-world-txn.schema.json`、`cross-world-txn-partial-commit.json`。
- **问题与直接证据**：`coordination` 规定先执行 Voxel Commit，再执行 ECS CommandBuffer Commit；但 `command` 仍声明 `commit` 失败时可以整批拒绝或回滚。公共基线却要求所有可能失败的业务检查、容量检查和 Chunk 可用性检查都在 Prepare 完成，Commit 后的 Apply 必须幂等，且不能再次发生业务校验失败。也就是说，Runtime 当前允许第二个参与者在第一个参与者已经产生权威写入后正常拒绝。
- **实际影响**：即使没有进程崩溃，也可能形成“Voxel 已提交、ECS 未提交”的正常执行路径。该状态无法依靠普通 CommandBuffer 回滚安全修复，会导致 GameRevision 与 VoxelRevision 分叉、库存扣除与方块落地不一致、Replay 不确定，以及持久化后无法判断真实业务结果。
- **建议修复**：
  1. 在 `CrossWorldPrepare` 中增加 ECS 侧完整 Preflight/Reservation，生成不可变的 `PreparedGameDelta`。
  2. CommandBuffer 状态机扩展为 `Open -> Sealed -> Merged -> Prepared -> Applied`。
  3. Generation、目标存在性、组件容量、命令冲突、权限、预算等业务拒绝必须在 `Prepared` 前完成。
  4. `CommitIntent` 之后，ECS Apply 只能返回 `Applied`、`AlreadyApplied` 或基础设施级 `Indeterminate/Faulted`，不能返回普通业务拒绝。
  5. 基础设施故障进入参与者查询与 Journal 恢复，而不是声称“整批回滚”已经成功。
- **阻塞**：Architecture Gate、Foundation、`RT-D-003`、`RT-D-004`、首个 `PlaceVoxelAbility` Vertical Slice。

---

### P0 — RT-AR-002：普通 Component 字段写入缺少 Tick 级失败原子性

- **类型**：Decision Gap，具有直接 P0 风险。
- **位置**：
  - Runtime `modules/ecs/README.md:63-65`
  - Runtime `modules/simulation/README.md:64-67,69-80`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:147-165`
- **关联架构源**：§4.1、§4.2、§4.4、ADR-002、Processor 异常与 Replay Failure Matrix。
- **问题与直接证据**：ECS 文档允许 Processor 通过 View 直接写已有组件字段；结构变化才进入 CommandBuffer。Simulation 对取消、QueueFull 和超预算的保证仅明确到“不能部分应用结构写入”，没有说明 Processor 已经直接写入的字段如何撤销、隔离或丢弃。公共架构要求每个 Phase 明确输入、可写状态、错误处理和可见性，但当前没有逐 Phase 契约矩阵，也没有 Tick 的权威提交点。
- **实际影响**：Processor 在写入部分字段后抛异常、预算中止或取消时，World 可能残留半个 Tick 的可见权威状态。随后 Hash、Replication、Snapshot 或下一 Tick 可能观察到该状态，造成确定性失效和无法可靠重放。
- **建议修复**：必须在实现前选择并冻结一种模型：
  - **Staged Write 模型**：所有权威字段写入先进入 Tick WriteSet/Overlay，固定 Barrier 一次发布；或
  - **Fail-stop Recovery 模型**：字段可以原地写，但任一提交前故障都使当前 World 不可继续使用，必须从 Tick 前 Snapshot/Undo Journal 重建。

  同时补充 13 个 Phase 的矩阵：输入、允许写入的状态域、失败分类、取消点、超预算动作、对后续 Phase 的可见性、Tick Result、重复 Tick 的幂等结果及最终 Commit Point。
- **阻塞**：Architecture Gate、Foundation ECS/Tick 闭环、`RT-D-001`、`RT-D-003`、`RT-D-011`。

---

### P1 — RT-AR-003：`SimulationSession` 状态所有权自相矛盾

- **类型**：事实性文档冲突。
- **位置**：
  - Runtime `README.md:19`
  - Runtime `modules/README.md:123,135`
  - Runtime `modules/simulation/README.md:40`
- **关联架构源**：§2.3 RACI、§3 Session 拓扑、§6 Revision/Txn。
- **问题与直接证据**：根 README 在“拥有的状态与生命周期”中称 `SimulationSession` 拥有 Revision Vector 和 Coordinator 状态；模块总文档与 simulation README 则明确它只是生命周期 Facade，Revision、Txn、Reservation、SnapshotCut 的真实状态由 `coordination` 唯一持有。
- **实际影响**：首个 C# API 很容易在 `SimulationSession` 和 `Coordinator` 中各缓存一份 Revision 或 Txn 状态，形成双写、陈旧读取和恢复状态分叉。
- **建议修复**：将根 README 改为“`SimulationSession` 对外聚合/暴露 Logical Tick 与 Coordinator Facade”，明确：
  - `simulation` 唯一拥有 Tick、Phase、Plan、Determinism；
  - `coordination` 唯一拥有 Revision、Txn、Reservation、SnapshotCut；
  - Facade 只能转发查询或命令，不能缓存第二份可变状态。
- **阻塞**：`RT-D-001`、Foundation Assembly/API 设计、Coordinator 生命周期实现。

---

### P1 — RT-AR-004：Txn 状态图与可执行恢复语义冲突

- **类型**：公共架构源与 Runtime 同时存在的 Decision Gap，不能只在本仓修复。
- **位置**：
  - Runtime `modules/coordination/README.md:52-57`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:247-261`
  - Architecture `tools/lumio_contract.py:254-258`
- **关联架构源**：ADR-003、`cross-world-txn.schema.json`、Partial Commit/Lost Result/Crash Fixture。
- **问题与直接证据**：Runtime 和公共架构状态图都允许 `Prepared -> Indeterminate`；但 Contract Validator 只接受 `commitIntentPersisted=true` 且恰有一个参与者完成标记的 `Indeterminate`。因此“仅 Prepared、尚未写 CommitIntent”的事务不可能成为 Validator 定义的 Indeterminate。与此同时，参与者已 Apply、但完成标记尚未来得及落盘的崩溃窗口也无法用“恰有一个 marker=true”准确表示。
- **实际影响**：状态查询、重复提交和崩溃恢复可能对同一记录给出不同解释；某些真实崩溃状态无法编码为合法契约对象。
- **建议修复**：
  - 在公共架构源中把 pre-intent 路径改为 `Prepared -> Aborted/Expired`。
  - `Indeterminate` 只能从已持久化 CommitIntent 的 Apply 阶段进入。
  - 参与者状态不要仅使用 Boolean，至少表达 `NotStarted / Unknown / Applied / Failed`。
  - 明确“Apply 成功但 participant marker 未持久化”的查询及恢复算法。
  - 同步更新 ADR、Schema、Validator、正向/失败 Fixture 和 Runtime 镜像。
- **阻塞**：Architecture Gate、`RT-D-004`、Crash Recovery、Lost Result 测试。

---

### P1 — RT-AR-005：Replication 的 Voxel 原子应用没有对应依赖端口

- **类型**：职责与依赖缺口。
- **位置**：
  - Runtime `modules/replication/README.md:11,18,33,56`
  - Runtime `modules/README.md:95-99`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:269-286`
- **关联架构源**：§7.2 PredictionFrame、ADR-005、ADR-009、Generated Voxel Contract。
- **问题与直接证据**：Replication 明确声明 Client 侧把 ECS、GAS 和 Voxel Overlay 作为同一确认/回滚单元原子应用，但模块依赖只列出 ECS、GAS、Coordination、Config、Observability，DAG 中也没有 `replication -> Generated Voxel Contract`。
- **实际影响**：实现时只能选择以下错误路径之一：直接依赖 VoxelEngine 内部、把 Voxel Apply 偷渡给 Host、通过 Coordination 间接调用不适用于 Client Replica 的接口，或根本无法保证三域原子回滚。
- **建议修复**：在 Runtime 边界中增加版本化的 Voxel Replica Port，例如：
  - `PrepareAuthoritativeOverlay`
  - `ApplyPreparedOverlay`
  - `RollbackToPredictionFrame`
  - `CaptureReplicaRevision`

  端口只暴露 generated contract、Revision、Token 和幂等结果，不暴露 Chunk Storage。将该依赖补入 DAG、Replication README、ReferenceVoxelPort 和 Differential Test。
- **阻塞**：Replication Foundation、Prediction、LocalEmbedded/LocalSplitProcess Vertical Slice、`RT-D-005`。

---

### P1 — RT-AR-006：SnapshotCut 的 Provider 集合遗漏 Voxel

- **类型**：状态一致性与恢复闭环缺口。
- **位置**：
  - Runtime `modules/persistence/README.md:14,40`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:103-105,219-231,322-326`
- **关联架构源**：§3.1、§6.1、§11.1、ADR-010、Snapshot Header。
- **问题与直接证据**：Persistence 只列出 ECS、GAS、Replication、Config Provider，没有 Voxel Provider；但公共架构的 Session 同时包含 GameWorld、VoxelWorld 和 Coordinator/SnapshotCut，且 SnapshotCut 必须固定同一 SessionRevisionVector。持久化基线也明确包含 Chunk/Voxel 数据。
- **实际影响**：恢复出的 ECS/GAS 可能对应一个不同 Voxel Revision；CrossWorldTxn 即使提交正确，也可能在 Snapshot/Restore 后重新产生跨域分叉。
- **建议修复**：
  - `coordination` 固定的 SnapshotCut 必须包含 Voxel Snapshot Token/Revision。
  - `persistence` 通过 Generated Voxel Snapshot Provider 获取不可变引用、Chunk Manifest 或内容寻址对象。
  - Snapshot Manifest 必须记录所有参与者的 Revision、Hash、SchemaEpoch 和 Provider Result。
  - Runtime 不复制 Voxel Storage；Host 仍只提供耐久介质。
- **阻塞**：Snapshot/WAL Vertical Slice、Crash Recovery、`RT-D-007`、跨域 State Hash。

---

### P1 — RT-AR-007：Replication Wire 契约不足以表示 V1 协议

- **类型**：公共契约不完整。
- **位置**：
  - Architecture `schemas/replication-envelope.schema.json:5-47`
  - Architecture `fixtures/valid/replication-full-snapshot.json:2-14`
  - Architecture `schemas/index.json:4-22`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:269-286`
- **关联架构源**：§7.1、§7.3、ADR-005、ADR-009、Replication Fixtures。
- **问题与直接证据**：
  - `FullSnapshot` 在架构中必须关联 `SnapshotId + TickId + RevisionVector`，但 Schema 与正例 Fixture 没有必填 `TickId` 或完整 RevisionVector。
  - 没有结构化 `SchemaEpoch`、Mapping ID/version/hash、Prediction confirmation、权限上下文或消息体类型约束。
  - `payload` 可以是任意 JSON 值，无法验证不同 `messageType` 的必填字段。
  - `length` 只有非负限制，没有最大消息、分片预算或压缩后分配上限。
  - 公共架构要求最大长度、分片、重传、反重放和认证，但当前 Schema 集中没有对应消息体或策略契约。
- **实际影响**：两个实现都可以通过当前 Contract Validator，却对 Snapshot Revision、Mapping 版本和 Resync 条件做出不同解释；LocalEmbedded 也无法证明自己走了与远程连接相同的完整协议。
- **建议修复**：
  1. 将 Envelope 与消息体分离。
  2. 为 Handshake、FullSnapshot、BaselineAck、Delta、DeltaAck、ResyncRequest、Error 建立 discriminated schema 或独立 body schema。
  3. FullSnapshot 强制携带 TickId、完整 SessionRevisionVector、SchemaEpoch、MappingSetHash。
  4. Delta 强制携带 BaseSnapshot、From/To Revision、MappingSetHash、确认序列和 Tombstone 信息。
  5. 在 Host Transport Profile 或 Envelope 契约中显式冻结最大消息、分片、反重放窗口、认证绑定和错误分类。
- **阻塞**：Architecture Gate、Replication Foundation、LocalEmbedded Fidelity、Fuzz/Resync 测试。

---

### P1 — RT-AR-008：Wire MessageType 与 ID Registry 漂移

- **类型**：公共 ID 契约冲突。
- **位置**：
  - Architecture `schemas/replication-envelope.schema.json:25-27`
  - Architecture `ids/index.json:6-13`
  - Architecture `tools/lumio_contract.py:343-352`
- **关联架构源**：ID Registry、Replication Envelope、Architecture Gate。
- **问题与直接证据**：Envelope 枚举包含 `BaselineAck`、`DeltaAck` 和 `Error`，公共 `MessageType` Registry 却只登记 Handshake、FullSnapshot、Delta、ResyncRequest、MaintenanceKick。Validator 只检查 Registry 内部 ID 和数值不重复，没有检查 Schema 枚举与 Registry 的集合一致性。
- **实际影响**：生成器无法为三个合法 Wire 消息产生稳定 Numeric ID；不同语言实现可能自行分配编号，造成序列化不兼容。
- **建议修复**：在架构源登记全部 Active/Reserved MessageType，并让 Contract Tool 自动比较：
  - Schema enum；
  - ID Registry；
  - Fixture 中实际使用的 messageType；
  - 生成代码中的 Numeric ID。
- **阻塞**：公共协议生成、Native/Managed/Host 跨语言集成、Architecture Gate。

---

### P1 — RT-AR-009：Entity Identity 可以省略命名空间

- **类型**：公共身份与安全边界缺陷。
- **位置**：
  - Architecture `schemas/entity-identity.schema.json:5-13`
  - Architecture `tools/lumio_contract.py:274-283`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:199-204`
- **关联架构源**：ADR-004、Entity Identity Schema、Tombstone/Provisional Fixtures。
- **问题与直接证据**：Schema 定义了 `Authoritative / Provisional / Replay` 命名空间，但未把 `namespace` 放入 required；Validator 只在字段值恰好为 `Provisional` 时执行 provisional 约束。省略 namespace 的对象可以绕过身份域验证。
- **实际影响**：临时预测 ID、Replay ID 和权威 ID 可能进入同一处理路径；映射、权限、Tombstone 和防重用逻辑失去可靠判别依据。
- **建议修复**：
  - `namespace` 必填。
  - 使用条件 Schema 约束各命名空间的 authorityDomain、remap、lifecycle。
  - Provisional ID 必须有客户端 authority domain；Authoritative ID 禁止使用 provisional domain。
  - Replay/Migration 保留原 ID 时，增加来源 Revision/Release 约束。
  - 增加“missing namespace”失败 Fixture。
- **阻塞**：Entity/Replication Foundation、Prediction Remap、Replay/Migration。

---

### P1 — RT-AR-010：ProcessorDescriptor Validator 编码了错误或未决的执行语义

- **类型**：Schema、Validator 与 Runtime 执行模型冲突。
- **位置**：
  - Architecture `schemas/processor-descriptor.schema.json:5-13`
  - Architecture `tools/lumio_contract.py:332-337`
  - Architecture `fixtures/invalid/processor-read-write-conflict.json:2-12`
  - Runtime `modules/command/README.md:14-17,61-64`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:168-175,212-213`
- **关联架构源**：§4.2、§5.4、ADR-002。
- **问题与直接证据**：
  1. Validator 要求 `structuralWrites=true` 的 Processor 本身位于 `EcsCommandBufferCommit`；但 Runtime 设计是 Gameplay Processor 在自身执行 Phase 产生结构命令，统一到后续 Commit Phase 应用。
  2. Validator 把 Stable Processor 自身 ReadSet 与 WriteSet 重叠定义为失败；公共架构只要求 Scheduler 在执行前处理 Processor 之间的冲突，没有冻结“同一 Processor 不能读取并更新同一资源”的规则。
- **实际影响**：合法的“读取库存并写回库存”或“在 ApplyInputs 生成结构命令”的 Processor 无法通过公共 Contract，开发者只能伪造 Phase、错误标记 StructuralWrites，或绕过 Validator。
- **建议修复**：
  - 将字段重命名或拆分为 `mayEmitStructuralCommands` 与内部 `directStructuralApply`。
  - Gameplay Processor 可以在允许的业务 Phase 声明并发出结构命令；只有 Runtime Commit Executor 能直接改变结构。
  - ReadSet/WriteSet 自重叠是否允许必须由 ADR 明确；Scheduler 应重点校验 Processor 之间的依赖和冲突。
  - 更新 PlaceVoxel 正例及字段更新正例。
- **阻塞**：Processor Plan、CommandBuffer Foundation、`RT-D-003`、Contract Generator。

---

### P1 — RT-AR-011：GAS 通用状态机所有权不明确

- **类型**：职责边界 Decision Gap。
- **位置**：
  - Runtime `modules/gas/README.md:14-17,45-52`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:303-305`
- **关联架构源**：§9 GAS Framework、`RT-D-006`。
- **问题与直接证据**：GAS README 一方面声明 Runtime 管理 Framework 生命周期、Stack、Duration、Cancel 和预测恢复，另一方面称 Ability/Effect 的“具体状态机”由 Content Schema 声明。公共架构则要求 V1 在 Runtime 层定义 Ability/Effect 状态机及顺序语义。当前没有列出通用状态、合法转移和非法转移。
- **实际影响**：不同 Game 内容可能各自发明 Activated、Running、Cancelled、Expired、Rejected 等语义，导致 Snapshot、Replication、Prediction 和 Hot Reload 无法使用统一恢复规则。
- **建议修复**：
  - Runtime/公共架构冻结最小通用状态机及状态转移。
  - Game 只声明 Type Descriptor、Formula、Cost、Targeting、Cooldown 数据和受约束 Hook。
  - 内容可以拥有业务子状态，但不能改变通用生命周期、终止语义、回滚窗口和 Handle 失效规则。
- **阻塞**：`RT-D-006`、GAS Vertical Slice、Prediction/Snapshot/Hash 集成。

---

### P1 — RT-AR-012：Durable Journal 只有日志类别，没有恢复记录契约

- **类型**：持久化公共契约缺失。
- **位置**：
  - Architecture `schemas/index.json:4-22`
  - Architecture `schemas/logging-event.schema.json:5-14`
  - Runtime `modules/persistence/README.md:14-17,27-35,62-70`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:322-350`
- **关联架构源**：§6.2、§11.1、§12、ADR-003、ADR-010、ADR-011。
- **问题与直接证据**：Schema Registry 没有独立的 TxnJournal Record、CommandLog Record 或 WAL Record Schema。`LoggingEvent` 虽然把 TxnJournal 和 CommandLog 列为 category，但载荷仍只是任意 `message + fields`；它不能表达追加顺序、前序 Hash、Commit Marker、恢复游标、幂等键或记录校验。公共架构明确说明这些是独立耐久恢复输入，不能由 Diagnostic Log 替代。
- **实际影响**：Persistence 和 Coordination 必须自行发明记录布局，形成第二套公共恢复协议；跨 Release、跨实现和故障恢复无法证明兼容。
- **建议修复**：在公共架构源定义至少以下外层记录契约：
  - `TxnJournalRecord`
  - `CommandLogRecord`
  - 必要时 `WalRecordEnvelope`

  每条记录包含 RecordVersion、RecordSeq、Session/Release/Tick/Txn/Command 关联、RecordKind、IdempotencyKey、PreviousHash、PayloadHash、Length、Commit/Durability 状态和 Checksum。业务 Command Payload 可以保持 Game-generated，但恢复记录 Envelope 必须统一。
- **阻塞**：`RT-D-004`、`RT-D-007`、可靠恢复、Crash-at-each-boundary 测试。

---

### P1 — RT-AR-013：Config P0 Schema 不验证 typed table 核心语义

- **类型**：公共 Schema 与模块承诺不一致。
- **位置**：
  - Architecture `schemas/config-table.schema.json:5-38`
  - Architecture `tools/lumio_contract.py:311-323`
  - Runtime `modules/config/README.md:14-16,34-36,60-65`
  - Architecture `fixtures/index.json:25-26`
- **关联架构源**：§11.3、ADR-010、`RT-D-008`。
- **问题与直接证据**：
  - `columns`、`activation` 不是 required。
  - 每一行的 `values` 是完全无约束的 object。
  - Validator 只检查 Key/Column 唯一、必填列名称和生产签名，不检查值是否符合列类型，也不检查范围、枚举、引用或未知列。
  - 当前失败 Fixture 只覆盖重复 Key。

  这与 Runtime 声明的类型、范围、引用和缺列拒绝能力不一致。
- **实际影响**：一个列声明为 `u32`、实际值为字符串，或一个无效引用的表仍可能通过架构源 Validator；Runtime 实现只能再次自行定义规则。
- **建议修复**：
  - 将 columns 和 activation 纳入必要条件。
  - 生成每张表的专用 Schema/Validator，或在 Contract Tool 中根据列描述执行动态类型校验。
  - 定义范围、枚举集合、引用目标、未知列策略、默认值和数值规范化规则。
  - 增加 type mismatch、range overflow、missing ref、unknown required column、production unsigned 等失败 Fixture。
- **阻塞**：Config Vertical Slice、`RT-D-008`、Production Signed Switch。

---

### P1 — RT-AR-014：Hot Reload 缺少可证明的原子激活协议

- **类型**：生命周期与故障恢复 Decision Gap。
- **位置**：
  - Runtime `modules/hot-reload/README.md:14-17,44-50,60-69`
  - Architecture `.spec/decisions/ADR-013-migration-dag.md:11-18`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:373-375`
- **关联架构源**：ADR-013、`RT-D-010`、Migration Manifest。
- **问题与直接证据**：Hot Reload 文档完整描述了旧 Scope 的 Quiesce/Cancel/Drain/Dispose/Unload，却没有定义新 Scope 何时创建、验证、迁移、绑定 World，以及哪个 Tick Barrier 原子切换。正常流程文字甚至可被理解为先卸载旧 ALC，再在 Staging 生成新 Scope；这与“失败保留旧有效指针”的 Migration 原则不一致。
- **实际影响**：新 Gameplay 验证或迁移失败时可能已经失去旧 Scope；若旧/新 Scope 同时接收事件，又可能产生双写和权威 World 污染。
- **建议修复**：定义双 Scope 状态机，例如：

  `OldActive + NewStaging -> NewValidated -> BarrierSwitch -> OldQuiescing -> OldUnloaded`

  并明确：
  - BarrierSwitch 前失败：丢弃 NewStaging，OldActive 不变；
  - BarrierSwitch 后失败：不得重新激活已排空或 Dispose 的旧 Scope，应使 Session Faulted 并从有效 Snapshot/Release 恢复；
  - Migration 只能读不可变 Snapshot，在 Staging 生成结果；
  - 所有入口、订阅、Timer、Task 和 Native Lease 在切换点按 Scope Generation 线性化。
- **阻塞**：`RT-D-010`、Hot Reload Vertical Slice、连续 Soak 和故障域验证。

---

### P2 — RT-AR-015：依赖 DAG 遗漏实际依赖并混合两种关系

- **类型**：文档与装配 Decision Gap。
- **位置**：
  - Runtime `modules/README.md:62-119`
  - Runtime `modules/gas/README.md:39-41`
  - Runtime `modules/replication/README.md:38-41`
  - Runtime `modules/ecs/README.md:39-41`
- **关联架构源**：Repository Architecture、`RT-D-001`。
- **问题与直接证据**：总图将箭头定义为“编译期/逻辑依赖”，但这两种关系并不等价；同时 GAS 实际消费 Config、Replication 实际消费 Command confirmation sequence、ECS 实际消费 Observability event carrier，图中没有对应边。
- **实际影响**：首次拆分 `.csproj` 时可能发现隐藏引用，或者为避免循环而把大量接口放入不受控的 Common 程序集。
- **建议修复**：分别维护：
  1. Assembly/compile DAG；
  2. Runtime call sequence；
  3. State ownership table。

  明确 TickContext、PhaseId、EventPort、JournalPort、VoxelPort 等中立契约属于哪个 Assembly。补充或消除隐藏边。
- **阻塞**：`RT-D-001`；不单独阻塞公共架构方向，但会阻塞首批工程拆分。

---

### P2 — RT-AR-016：队列和资源限制尚未形成统一可执行契约

- **类型**：实现准备度 Decision Gap。
- **位置**：
  - Runtime `modules/README.md:198-200`
  - Runtime `modules/simulation/README.md:64-80`
  - Runtime `modules/command/README.md:54-57`
  - Runtime `modules/replication/README.md:55-58`
  - Runtime `modules/persistence/README.md:61-64`
  - Runtime `modules/observability/README.md:53-55`
- **关联架构源**：§4.3、§12.1、`RT-D-003/004/005/009/010`。
- **问题与直接证据**：各模块普遍声明“有界”，但缺少统一的队列名称、容量单位、配置来源、可靠性等级、满载动作、超时、取消、重试、幂等键和故障升级目标。Simulation 的“停止当前阶段或按策略降级”、Command 的“按优先级拒绝或取消”仍不足以直接实现。
- **实际影响**：不同模块可能对同一 QueueFull 分别采取丢弃、重试、暂停 Tick 或 Session Fault，破坏可预测性。
- **建议修复**：增加 Queue Contract Matrix。数值可以保持 Config/Capability 参数，不必提前硬编码，但每条队列必须声明 producer、consumer/barrier、容量单位、可靠性、full action、deadline、ordering、idempotency、metric 和 escalation。
- **阻塞**：各模块 Stress/Soak 与容量决策；非独立 Architecture Gate P0。

---

### P2 — RT-AR-017：Tombstone 保留下界不可验证

- **类型**：Decision Gap。
- **位置**：
  - Runtime `README.md:69-71`
  - Runtime `modules/replication/README.md:26-29,57-58`
  - Runtime `modules/README.md:250`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:199-204`
  - Architecture `schemas/entity-identity.schema.json:13-15`
- **关联架构源**：ADR-004、`RT-D-005`。
- **问题与直接证据**：文档要求 Tombstone 保留到 Baseline Ack/失效，但没有定义它与 Delta History、断线重连、Prediction rollback、未确认 Baseline 和 Mapping 生命周期的关系。Schema 只保存一个 Revision 数值，无法证明该值覆盖所有仍可能引用旧 ID 的上下文。
- **实际影响**：过早回收可能使迟到 Delta 复活或错误命中重用 ID；过晚回收则造成映射与历史内存无界增长。
- **建议修复**：冻结保留下界：

  `TombstoneHorizon >= max(outstanding baseline horizon, retained delta history, reconnect window, prediction rollback window, migration/replay pin)`

  GC 前检查所有 ReplicationContext 的引用，并增加 outstanding baseline 下错误 GC 的失败 Fixture。
- **阻塞**：`RT-D-005` 和 Replication Soak，不单独阻塞模块划分。

---

### P2 — RT-AR-018：Config 编译器所有权模糊

- **类型**：职责边界 Decision Gap。
- **位置**：
  - Runtime `modules/config/README.md:10-17,33-41`
  - Architecture `schemas/index.json:12`
  - Architecture `docs/architecture/LumioGameEngine_Architecture_v1.0.md:428-431`
- **关联架构源**：§11.3、§15 Toolchain、ADR-010、`RT-D-008`。
- **问题与直接证据**：Config 模块既接收人类可读源，又暴露 `compile` 候选接口；但公共 Schema 把 Config Table 的 owner 定义为 Game，仓库地图也把具体 Config/Content 放在 Game。当前无法判断稳定 Runtime 是否需要承担内容编译器职责。
- **实际影响**：Runtime 可能逐渐吸收文件格式、默认值、内容引用和开发文件监听，形成对 Game 内容与文件系统的隐性依赖。
- **建议修复**：默认边界应为：
  - Game/Toolchain：源文件、默认值、生成、内容引用解析；
  - Runtime Config：验证生成物、层级合并、Staging、签名检查、Tick 边界激活、typed reader。

  若开发 Hot Load 必须在 Runtime 进程内编译，应通过独立 Dev Capability/Adapter 和本仓 ADR 明确。
- **阻塞**：`RT-D-008`，非 P0。

---

### P2 — RT-AR-019：FailureBundle 无法表示 pre-snapshot 故障

- **类型**：公共 Schema 覆盖缺口。
- **位置**：
  - Architecture `schemas/failure-bundle.schema.json:5-12`
  - Runtime `modules/observability/README.md:59-64`
- **关联架构源**：ADR-011、Failure Bundle Fixture。
- **问题与直接证据**：FailureBundle 把 `snapshotId` 设为必填，Observability 流程也假定导出前验证 Snapshot。但 ABI/Capability 启动失败、首次 Tick OOM、首个 Snapshot 前的 Scope 泄漏等故障可能不存在有效 Snapshot。
- **实际影响**：最早期、通常最难排查的故障无法生成符合公共 Schema 的 FailureBundle，只能丢失证据或伪造 Snapshot ID。
- **建议修复**：使用条件结构，要求以下二者之一：
  - `snapshotId`；或
  - `noSnapshotReason + bootstrapPhase + lastKnownRevision/manifest`。
- **阻塞**：启动故障与 Hot Reload Failure Bundle 完整性，非 Foundation P0。

---

### P3 — RT-AR-020：根 README 的日志队列表述不够准确

- **类型**：非阻塞文档一致性问题。
- **位置**：
  - Runtime `README.md:92`
  - Runtime `modules/README.md:194-196`
  - Runtime `modules/observability/README.md:53-55,67-70`
- **关联架构源**：§12 Logging/Audit。
- **问题与直接证据**：根 README 可被理解为 Diagnostic、Audit、Txn、Command、Metric、Trace 都经同一种有界异步路径输出；更详细的模块文档则正确区分可采样队列和独立耐久队列。
- **实际影响**：新实现者只阅读根 README 时可能错误复用 BestEffort 日志队列承载恢复记录。
- **建议修复**：将根 README 改为“Diagnostic/Trace/Metrics 使用有界异步队列；Audit/TxnJournal/CommandLog 使用独立耐久路径”。
- **阻塞**：无；应随下一次文档修订处理。

---

## 2. 总体裁决

### 裁决：**退回，Architecture Gate 未通过**

退回原因不是“尚无 C# 实现”，而是当前文档和公共契约仍不能安全、唯一地指导实现：

1. CrossWorldTxn 存在正常路径下的跨域半提交风险。
2. 普通 ECS 字段写入缺少 Tick 级失败原子性。
3. Replication、Entity、Processor、Config、Journal 等 P0/P1 公共契约存在不可表达或互相矛盾的部分。
4. Voxel 在 Replication Apply 与 SnapshotCut 两条关键链路中缺少正式 Port。
5. SimulationSession、Txn Indeterminate、GAS 状态机和 Hot Reload 激活点仍存在状态所有权或状态机歧义。

整体方向并非错误。以下设计应保留：

- Host Wall Clock 与 Runtime Logical Tick 分离；
- 13 相固定顺序；
- V1 权威 World 单线程写入；
- Coordination 独占 Revision/Txn/SnapshotCut；
- Transport 与 Replication 语义分离；
- ECS 作为 GAS 权威可复制状态的单一真相；
- LocalEmbedded 不绕过协议；
- Persistence 与 Observability 分离；
- Hot Reload Scope 不拥有 CoreCLR/ALC；
- `testing` 只有出向测试依赖，不成为生产 Runtime 依赖。

`command` 和 `testing` 作为本仓实现粒度的独立模块是合理的，不属于过度拆分，也没有新增公共架构语义。当前声明的 DAG 本身未形成显式环；主要问题是缺边、端口归属和契约不完整。

---

## 3. 模块职责矩阵

| 模块 | 唯一职责与状态 | 明确不负责 | 关键依赖 | 阶段 | 审查结论 |
|---|---|---|---|---|---|
| `ecs` | World-local Entity、Component Storage、Query、View、ChangeSet | Tick 编排、Net ID、Txn、持久化后端 | Generated Component Contract、Event Port | Foundation | 边界合理；字段写入失败原子性未闭合，见 RT-AR-002 |
| `simulation` | Logical Tick、Phase、Processor Plan、Determinism、Tick Result | Wall Clock、Socket、ECS Storage、Txn 状态 | ECS、Command、Coordination、GAS、Replication、Config | Architecture/Foundation | 编排职责正确；必须保持 Facade，不持有 Revision/Txn |
| `command` | Processor-local Buffer、Deferred Token、稳定合并、结构 Apply | 跨 World Txn、Voxel、网络、持久化 | ECS、Tick Context、Event Port | Foundation | 拆分合理；缺少 `Prepared`/Preflight 边界 |
| `coordination` | Revision Vector、CrossWorldTxn、Reservation、SnapshotCut | Voxel/ECS 数据、存储后端、Host 时钟 | ECS Prepared Apply、Command、Voxel Txn Port、Journal Port | Architecture/Foundation | 唯一所有者定位正确；状态图与恢复契约需修正 |
| `replication` | Mapping、Net/Local 映射、History、Baseline/Delta、Apply、Resync | Socket、Connection、Transport ACK、具体 AOI 内容 | ECS、GAS、Coordination、Config、Generated Mapping、Voxel Replica Port | Architecture/Foundation | Transport 边界正确；Voxel Port 和 Wire 契约缺失 |
| `gas` | 通用生命周期、Handle、Stack/Duration/Cancel、Prediction Context、ECS 投影 | 具体 Formula、Cost、Targeting、表现、内容资产 | ECS、Command、Config、Content Descriptor | Vertical Slice | 模块合理；通用状态机必须由 Runtime 冻结 |
| `persistence` | Snapshot Codec、Staging/Activation、WAL/Command/Txn Adapter、恢复游标 | Txn 状态机、领域字段、具体介质 | SnapshotCut、各 Provider、Host Storage Adapter | Vertical Slice | 边界方向正确；遗漏 Voxel Provider 和 Record Schema |
| `config` | 生成表验证、层级合并、不可变 ConfigSnapshot、Tick 激活 | Secret、业务规则、通用持久化 | Game-generated table、Signature/Capability | Vertical Slice | Runtime Reader/Activation 合理；编译器职责需拆清 |
| `observability` | EventSeq、Correlation、路由、Metrics/Trace、FailureBundle 组装 | 恢复真相、Session 故障处置、具体 Sink | Config、Host Sink Adapter | Vertical/Hardening | 类别分层合理；LoggingEvent 不能充当 Journal Record |
| `hot-reload` | GameplayModuleScope、Lease、资源登记、卸载证据、Migration Hook | ALC/CoreCLR 创建、进程、Release Pool | Simulation Barrier、GAS、Config、Host ALC | Vertical/Hardening | 卸载协议较完整；原子新旧 Scope 切换缺失 |
| `testing` | Reference Host、Replay/Hash、Fixture Runner、Fault Adapter | 生产路径、测试后门、公共 Schema 所有权 | 所有公开 Runtime 接口，仅测试工程消费 | Foundation/Vertical | 边界正确；`RT-D-011` 和实际保真基线仍待建立 |

模块 README 的统一章节契约基本得到遵守，职责、非职责、状态、输入输出、失败面和测试面都已有覆盖。问题集中在关键状态机是否形成单一可执行语义，而不是模块数量不足。

---

## 4. 依赖 DAG 审查

### 4.1 显式环

当前 `modules/README.md` 所列边不存在显式循环：

- `testing` 只有出向依赖；
- `config`、`observability` 处于基础层；
- `ecs` 不反向依赖 simulation；
- `persistence` 消费 Provider，不拥有 Tick；
- `hot-reload` 消费 simulation/gas/config，但不创建 Host Runtime；
- `coordination` 通过 Voxel Contract，而非 VoxelEngine 实现源码。

### 4.2 潜在编译环

最大风险是“接口注入”没有规定接口类型属于哪个 Assembly。例如：

- `coordination` 需要耐久 `ITxnJournalWriter`；
- `persistence` 又消费 Coordination Snapshot/Txn 类型；
- 若 Journal Port 定义在 persistence Assembly，便会形成 `coordination <-> persistence` 编译环。

应采用依赖倒置：Port 契约属于调用方或中立 generated-contract Assembly，具体实现由 Composition Root 注入。相同规则适用于 Observability Event Port、Voxel Port、TickContext 和 Replication confirmation 类型。

### 4.3 缺失或需澄清的边

| 关系 | 当前状态 | 建议 |
|---|---|---|
| `replication -> Generated Voxel Replica Contract` | 缺失 | 必须补充 |
| `persistence -> Generated Voxel Snapshot Contract` | 缺失 | 必须补充 |
| `gas -> config` | 模块 README 有，DAG 无 | 补边，或声明通过中立 `IConfigSnapshot` |
| `replication -> command` | 文档消费确认序号，DAG 无 | 补充逻辑边；必要时避免 Assembly 直接引用 |
| `ecs -> observability` | 模块 README 有，DAG 无 | 通过基础 Event Port 表达 |
| `coordination -> durable journal port` | 仅文字说明注入 | 明确 Port 所属 Assembly |
| `hot-reload -> Host ALC` | 运行时调用关系，不应是 Runtime 编译依赖 | 在 Host Adapter 图中单独表示 |

### 4.4 必须分开的三种关系

| 关系 | 说明 |
|---|---|
| **编译依赖** | `.csproj`/Assembly reference；必须严格无环 |
| **运行时调用方向** | Simulation 编排下游、Host 回调入队、Worker 返回 Completion；可以与编译依赖不同 |
| **状态所有权** | 哪个模块持有可变真相；不能从“谁调用谁”推断 |

建议将现有 Mermaid 图重命名为“逻辑消费关系”，另建 Assembly DAG。`RT-D-001` 未批准前，不应把逻辑模块数量直接等同为程序集数量。

---

## 5. 状态所有权审查

| 状态/资源 | 唯一所有者 | 允许的消费者或 Facade | 裁定 |
|---|---|---|---|
| Entity、Component Storage、Query View、ChangeSet | `ecs` | simulation、gas、replication、persistence | 清楚 |
| Logical TickId、Phase Graph、Processor Plan、Determinism Context | `simulation` | Host 只调用单一入口 | 清楚 |
| CommandBuffer、Deferred Token、Merge/Apply Result | `command` | simulation、coordination、gas | 清楚，但需增加 Prepared 状态 |
| SessionRevisionVector、Txn、Reservation、SnapshotCut | `coordination` | SimulationSession 仅 Facade；replication/persistence 只读或提交请求 | 模块文档清楚，根 README 冲突 |
| Mapping、Net/Local 映射、History、Tombstone、Apply Context | `replication` | Host Transport 只传 Envelope | 清楚 |
| GAS Registry、Handle、Prediction/Execution Context | `gas` | 权威字段写入 ECS；Game 提供 Descriptor/Hook | 状态机边界待冻结 |
| Snapshot Codec、Log Queue、Checkpoint、Recovery Cursor | `persistence` | Coordination 仍拥有事务状态机和 Cut | 清楚 |
| Active ConfigSnapshot、版本索引、激活队列 | `config` | 各模块只读 | 清楚 |
| Producer EventSeq、路由、采样状态、FailureBundle Assembly | `observability` | Host Sink；Persistence 持有 durable cursor | 基本清楚 |
| GameplayModuleScope、Lease、Unload Evidence | `hot-reload` | Host 持有 ALC/CoreCLR | 清楚；切换点未定义 |
| Reference Host、Fault Profile、Replay Result | `testing` | 测试工程 | 清楚 |
| Wall Clock、进程、Socket、Connection、Renderer、ALC 创建 | Host | Runtime 只消费 Capability/Port | 清楚 |
| Voxel Chunk/Block/Revision Storage | VoxelEngine | Runtime 仅经生成 Port | 清楚，但两条 Port 缺失 |

关键修正是：`SimulationSession` 可以拥有组合对象的生命周期，但不能因“聚合”而成为 Revision/Txn/SnapshotCut 的第二语义所有者。

---

## 6. Tick / Txn / Replication / Prediction 审查

### 6.1 Tick、Phase、Barrier 与 Determinism

| 审查项 | 状态 | 结论 |
|---|---|---|
| 13 相顺序 | 通过 | Runtime 与架构源完全一致 |
| Host 单一 Tick 入口 | 通过 | Host 决定何时进入；Runtime 决定内部语义 |
| Logical Tick 与 Wall Clock 分离 | 通过 | Runtime 明确不读取 Wall Clock |
| Processor Descriptor 预校验 | 部分通过 | 文档要求依赖环/冲突预拒绝，但 Validator 语义存在 RT-AR-010 |
| CommandBuffer 稳定合并 | 部分通过 | 排序键清楚；Prepared/Apply 不可失败边界缺失 |
| Native/IO Completion 只在 Barrier 应用 | 通过 | Owner Thread 与有界 Completion 规则一致 |
| 逐 Phase 输入/可写状态/失败/可见性 | 不通过 | 只有 Phase 名称和总体流程，没有 13 相契约矩阵 |
| 普通字段写入失败原子性 | 不通过 | RT-AR-002 |
| 迟到输入 | 部分通过 | 有三分类，但没有冻结 ArrivalClass、重复输入及切换边界 |
| 重复 Tick | Decision Gap | 测试面提到，但 API Result、ExpectedTick、幂等返回未定义 |
| 暂停/恢复/取消 | 部分通过 | 生命周期存在；Tick 中途取消点、当前 Tick 结果和输入归属未冻结 |
| Tick 超预算 | Decision Gap | “停止或降级”过于宽泛；权威写入可见性未定义 |
| Canonical Hash | 部分通过 | 明确排除对象地址、线程时序和缓存；但字段注册表、SchemaEpoch 与 Hash 版本未冻结 |

13 相链路和 Owner Thread 原则是正确的；真正的实现阻塞是“何时权威状态对后续 Phase 和外部可见”。

### 6.2 CrossWorldTxn、Revision 与恢复

| 审查项 | 状态 | 结论 |
|---|---|---|
| Revision 唯一所有者 | 通过 | `coordination` |
| Expected Game/Voxel/Chunk Revision、Deadline、权限/容量检查 | 文档通过 | 必须在 Prepare 完成 |
| Prepare 无可见副作用 | 通过 | 语义明确 |
| CommitIntent 在首写前持久化 | 通过 | 文档明确 |
| 固定 `Voxel -> ECS` 顺序 | 通过 | 文档和 Validator 一致 |
| Apply 幂等 | 设计要求通过 | 具体 ECS Apply 仍允许业务失败 |
| ECS PreparedDelta/Reservation | 不通过 | 未形成正式状态与接口 |
| 参与者完成标记 | 部分通过 | Boolean marker 无法完整表达未知状态 |
| Aborted/Committed/Indeterminate | 不通过 | 状态图和 Validator 冲突 |
| Duplicate/Lost Result 查询 | 文档通过 | 需要 durable record schema 支撑 |
| Crash Recovery | 不通过 | Record Schema、marker crash window 未闭合 |
| SnapshotCut 同一 Revision Vector | 部分通过 | Cut 所有权正确，但遗漏 Voxel Provider |

CrossWorldTxn 的高层方案正确，不需要引入重型 XA/通用 2PC；应修复的是本地双参与者协议的 Preflight、Journal 和恢复状态表达。

### 6.3 Entity、Replication 与 Prediction

| 审查项 | 状态 | 结论 |
|---|---|---|
| NetEntityId / LocalEntityId / Provisional 分层 | 文档通过 | Schema 的 namespace 必填性失败 |
| NetEntityId 不复用 | 通过 | 架构与 Runtime 一致 |
| Tombstone 防止迟到 Delta 复活 | 通过原则 | 保留窗口下界未冻结 |
| Respawn 新 ID | 通过 | Authority Transfer 正确保留为后置能力 |
| Mapping 字段覆盖 | 基本通过 | Source/Target、Role、Owner、可见性、可靠性、量化、预测、生命周期均有表达 |
| Baseline/Delta History | 部分通过 | Gap/Resync 路径正确；History 预算未决 |
| Schema/Mapping mismatch -> Full Resync | 文档通过 | Wire 无法强制携带相应版本/hash |
| Prediction Confirm/Reject/Rollback/Replay 顺序 | 文档通过 | 缺少 typed PredictionFrame/confirmation body contract |
| ECS/GAS/Voxel 原子确认单元 | 目标正确 | Voxel Replica Port 缺失 |
| LocalEmbedded 全协议路径 | 通过 | 明确不绕过 Serializer、Envelope、权限、大小和队列 |
| Transport/Connection 所有权 | 通过 | 正确留给 Host |

Replication 模块没有错误吞并 Socket 或 Connection 生命周期；问题主要位于公共 Wire 契约和 Voxel Adapter，而不是模块职责本身。

---

## 7. 并发、队列、资源和故障域审查

### 7.1 并发所有权

- V1 权威 World 单线程写入规则清楚。
- Worker 只能处理不可变 Snapshot 或返回有界 Completion。
- Native/IO/Transport 回调不能直接改变 World。
- CommandBuffer 可以并行生成，但合并和 Apply 在 Owner Thread 完成。
- 禁止把可变 World 引用、裸指针、Timer Delegate 或未登记 Task 放入异步队列。
- LocalEmbedded 的 Server/Client World 不共享对象引用。

这些原则符合 V1，当前不需要提前引入多写线程、并行 ECS Storage 或无锁 Event Bus。

### 7.2 队列与背压

| 队列类别 | 当前原则 | 缺口 |
|---|---|---|
| Ingress | 有界，按 ArrivalClass 处理 | 容量单位、客户端公平性、拒绝/断开条件 |
| Native Completion | 有界，Barrier 消费 | Deadline 后结果如何标记 stale，是否返回参与者查询 |
| CommandBuffer | 数量/命令/字节有上限 | Priority Registry、批次原子拒绝规则 |
| Replication History | 有界，耗尽 Full Resync | 与 Tombstone/Prediction/Reconnect 的统一预算 |
| Persistence | 满载停止新权威接入或维护 | durable ack、fsync 策略、重试 idempotency |
| Diagnostic | 可采样/丢弃 | 丢弃摘要和应急路径容量 |
| Audit/Txn/Command | 不可静默丢失 | 独立 Record Schema、停止接入的触发点 |
| Hot Reload Completion | 有界，按 Scope Generation 拒绝迟到结果 | 切换前后队列归属和 Drain 完成条件 |

### 7.3 故障域

| 故障 | 应属故障域 | 当前结论 |
|---|---|---|
| 业务拒绝、无权限、资源配额 | Command/Processor/Txn 请求 | 应在可见提交前拒绝 |
| Processor 可捕获异常 | Session/World | 是否可继续当前 World 尚未定义 |
| ECS/Txn/Determinism 不变量破坏 | Session Fault | 文档方向正确 |
| Replication Context 损坏 | 单 Connection/Context，可重建 | 文档方向正确 |
| Hot Gameplay 资源泄漏 | Scope；必要时升级 Session | 原子切换前后升级规则缺失 |
| ALC 无法卸载 | Host/Session，必要时进程 | 归属正确 |
| CoreCLR 崩溃、StackOverflow、OOM | 进程 | 归属正确 |
| Durable Journal 不可写 | 停止权威接入/维护 | 原则正确，记录契约缺失 |
| Voxel/ECS 两参与者结果不明 | Txn Indeterminate + Session 恢复 | 状态表达仍不完整 |

### 7.4 安全边界

文档已正确要求版本、长度、Role、Capability、权限、签名、Hash、资源预算和生成契约校验，也明确禁止 `IsLocal`/`IsOffline` 旁路和未经签名 Processor。主要缺口在于 Replication Envelope 尚不能结构化执行这些要求，而不是安全目标缺失。

---

## 8. 公共契约与架构源审查

### 8.1 基线判定

用户提供的 v0.3 文件明确只是 Deprecated Compatibility Pointer；规范性架构文件是 `LumioGameEngine_Architecture_v1.0.md`，Baseline 为 `LGE-V1.0-2026-08-27`，且不得向兼容指针添加新决策。

Runtime README 正确声明公共 Tick、Revision、Txn、Replication、Mapping、Snapshot、Failure Bundle 等契约只在 `LumioGameEngineArchitecture` 维护，Runtime 不应自行扩展字段。

### 8.2 公共契约审查结果

| 契约 | 当前状态 | 必须回到架构源处理的问题 |
|---|---|---|
| SessionRevisionVector | 基本完整 | Snapshot participant manifest/Provider 关联 |
| CrossWorldTxn | 方向正确 | Indeterminate 状态与 marker 恢复语义 |
| Replication Envelope | 不完整 | typed bodies、Tick/Revision、Mapping/Schema、认证与资源约束 |
| Entity Identity | 不完整 | namespace 必填与条件约束 |
| ProcessorDescriptor | 冲突 | StructuralWrites 与 Read/Write 语义 |
| Replication Mapping | 基本完整 | 与 Envelope 的 MappingSetHash/版本关联 |
| Snapshot Header | 基本方向正确 | 多 Provider manifest 与 Voxel Cut |
| Config Table | 不完整 | typed value、范围、引用及未知列 |
| LoggingEvent | 适合作为事件 | 不得作为 Txn/Command 恢复记录 |
| FailureBundle | 部分完整 | pre-snapshot failure |
| ID Registry | 漂移 | 补齐所有 Wire MessageType |
| Migration Manifest | 方向正确 | 与 Hot Reload Scope activation 的关系需明确 |

Schema/Fixture Registry 当前登记 19 个 Schema 和 43 个 Fixture，Contract Tool 也强制每个 P0 Schema 至少存在正向和失败 Fixture；但“存在 Fixture”不等于语义已完整。当前 Validator 对多个关键契约只覆盖了有限的不变量。

### 8.3 ADR 与决策门

外部架构源 ADR 当前主要处于 Architecture Gate Draft 状态；这不等于没有方向，但表示仍需要通过 Schema、Fixture 和跨仓验证后才能成为已验证基线。Runtime 本仓 `.spec/decisions/README.md` 明确当前没有本地 ADR，因此 `RT-D-001` 至 `RT-D-011` 均不能视为已批准。

以下问题必须回到公共架构源，不能只改 Runtime README：

- CrossWorldTxn Indeterminate 状态与 participant status；
- Replication Envelope/body、MessageType IDs；
- Entity namespace；
- ProcessorDescriptor 语义；
- GAS 通用状态机；
- Durable Journal Record Envelope；
- Config typed table 约束；
- 跨 Host/Game 的 Hot Reload 原子激活协议。

以下问题可在 Runtime 本仓 ADR 决定：

- 逻辑模块到程序集的映射；
- ECS 内部 Storage 结构；
- 具体 Queue 实现和容量参数；
- Adapter 使用的托管库；
- Reference Host 报告格式；
- 性能优化与内存布局。

---

## 9. 测试与实现就绪度审查

### 9.1 已具备的测试设计

`testing` 模块已经正确覆盖：

- PureHeadless、NativeHeadless、LocalEmbedded、LocalSplitProcess；
- Golden、Property、Fuzz、Stress/Soak、Differential、Fault、Replay；
- Canonical Hash 和首个 Tick/Phase/Processor 差异；
- QueueFull、OOM、磁盘满、ABI/Schema/Capability mismatch；
- Txn 冲突、超时、崩溃；
- Snapshot 损坏；
- ALC/Task/Timer/Handle 泄漏；
- 同一 Envelope、Serializer、权限、大小限制和有界队列路径。

`testing` 没有成为生产模块依赖，设计正确。

### 9.2 必须新增或修正的关键场景

在 Architecture Gate 关闭前，至少需要增加：

1. Voxel 已提交后 ECS Apply 发生业务拒绝必须在 Contract 层不可构造。
2. Processor 直接写字段后异常、取消、超预算的 Tick 原子性测试。
3. 重复 Tick 请求的幂等结果。
4. Prepared、CommitIntent、participant apply、participant marker 各崩溃点的恢复 Fixture。
5. FullSnapshot 缺失 TickId/RevisionVector/MappingSetHash 的失败 Fixture。
6. MessageType Schema 与 ID Registry 不一致检查。
7. Entity Identity 缺失 namespace 的失败 Fixture。
8. 允许合法 Processor 自读自写和发出结构命令的正例。
9. Config 类型错误、范围溢出、引用缺失、未知列失败 Fixture。
10. Outstanding Baseline 仍存在时错误 GC Tombstone 的失败场景。
11. 新 Scope 验证失败、BarrierSwitch 前失败、切换后失败的 Hot Reload 场景。
12. 首次 Snapshot 前的 FailureBundle。
13. Voxel Snapshot Revision 与 ECS Snapshot Revision 不一致的拒绝场景。

### 9.3 阶段就绪度

| 阶段 | 状态 | 判定 |
|---|---|---|
| Architecture Gate | **未通过** | P0/P1 状态机和 Schema 未闭合 |
| Foundation | **不应进入正式实现** | 可做隔离 Spike，但不能冻结 API |
| Vertical Slice | **未就绪** | Voxel Port、Wire、GAS、Persistence、Config 均有阻塞 |
| Production Hardening | **不可评估** | 尚无可运行实现和基线 |
| P2 | **无需提前设计** | 当前未发现把复杂 GAS、Sharding、Authority Transfer 等偷渡为 V1 必需能力 |

---

## 10. 必须修改项

| 修改位置 | 必须修改内容 | 对应 Finding | 阻塞 |
|---|---|---|---|
| Runtime + 公共架构源 | 增加 ECS Prepare/Reservation，保证 Voxel 后的 ECS Apply 不再业务失败 | RT-AR-001 | Architecture/Foundation |
| Runtime API Contract | 定义 Tick Commit Point、字段写入原子性及 13 相失败/可见性矩阵 | RT-AR-002 | Architecture/Foundation |
| Runtime 根 README | 将 SimulationSession 改为 Facade，Revision/Txn/SnapshotCut 唯一归 Coordination | RT-AR-003 | `RT-D-001` |
| 公共架构源 | 修正 Indeterminate 状态与参与者状态模型 | RT-AR-004 | `RT-D-004` |
| Runtime DAG/模块 README | 增加 Voxel Replica Apply Port 和 Voxel Snapshot Provider | RT-AR-005、006 | Replication/Persistence |
| 公共架构源 | 重构 Replication Envelope 与 typed message bodies | RT-AR-007 | Protocol Gate |
| 公共架构源 | 对齐 MessageType Enum 与 ID Registry | RT-AR-008 | Codegen |
| 公共架构源 | 将 Entity namespace 设为 required 并增加条件约束 | RT-AR-009 | Identity Gate |
| 公共架构源 | 修正 ProcessorDescriptor StructuralWrites、ReadSet/WriteSet 语义 | RT-AR-010 | Processor/Command |
| Runtime + 公共架构源 | 冻结 GAS 通用状态机、求值和回滚窗口 | RT-AR-011 | `RT-D-006` |
| 公共架构源 | 定义 TxnJournal/CommandLog/WAL Record Envelope | RT-AR-012 | Recovery |
| 公共架构源 | 完成 Config typed value/range/ref 校验 | RT-AR-013 | `RT-D-008` |
| Runtime + Host/Game ADR | 定义双 Scope Staging、BarrierSwitch 和失败后的唯一恢复动作 | RT-AR-014 | `RT-D-010` |

以上修改完成时，必须同步 ADR、Schema、ID、正向 Fixture、失败 Fixture、Validator、Baseline、Runtime 镜像和受影响 README；不能只修改说明文字。

---

## 11. 建议修改项

1. 将依赖图拆成 Assembly DAG、运行时调用图和状态所有权表，避免继续混用“编译期/逻辑依赖”。
2. 新增统一 Queue Contract Matrix，但容量值保持 Config/Capability 参数，不提前冻结未经测量的性能数字。
3. 将 Tombstone 保留窗口与 Baseline、History、Reconnect、Prediction 统一建模。
4. 默认把人类可读 Config 的编译放在 Game/Toolchain，Runtime 聚焦生成物验证和 Tick 激活。
5. 允许 FailureBundle 在不存在有效 Snapshot 时携带明确的 `noSnapshotReason`。
6. 修正根 README 的日志队列概括，明确 BestEffort 与 Durable 路径。
7. Snapshot 增加参与者 Manifest，而不是只提供单一不透明 payload。
8. `RT-D-001` 批准前避免创建泛化的 `Common`/`Utils` 大杂烩程序集；共享类型应按契约所有者放置。
9. 将所有候选接口持续标记为非冻结 API，直到对应公共 Schema、错误分类和 Fixture 通过。

---

## 12. 假设、未决问题和验证证据

### 12.1 审查范围与证据性质

本报告按照用户提供的“只读、对抗式、证据驱动”要求执行，审查目标是模块边界、职责、依赖、状态所有权、协议完整性和实现准备度，不以缺少 C# 代码本身作为缺陷。

本次能够访问并审查的是 GitHub 公共仓库 `main` 的原始文件以及会话内上传的兼容指针，无法访问用户机器上的：

- `/Users/cui/LumioGames/LumioGameRuntime`
- `/Users/cui/LumioGames/LumioGameEngineArchitecture`

因此：

- Findings 中行号是公共 `main` 原始文件的 **1-based 行号**，等价于对该公开版本执行 `nl -ba` 后的行号。
- 无法确认用户本地 checkout 是否与公共 `main` 完全一致。
- 无法确认本地工作区是否存在未提交修改。
- 没有修改、生成、覆盖或格式化任何仓库文件。

### 12.2 实际读取的 Runtime 文件

按要求顺序读取了：

1. `AGENTS.md`
2. `.spec/AGENTS.md`
3. `.spec/knowledge/README.md`
4. `.spec/rules/system.md`
5. `.spec/knowledge/standards/repository-architecture.md`
6. `.spec/knowledge/standards/code-style.md`
7. `.spec/knowledge/standards/testing.md`
8. `README.md`
9. `modules/README.md`
10. 全部模块 README：
    - `modules/ecs/README.md`
    - `modules/simulation/README.md`
    - `modules/command/README.md`
    - `modules/coordination/README.md`
    - `modules/replication/README.md`
    - `modules/gas/README.md`
    - `modules/persistence/README.md`
    - `modules/config/README.md`
    - `modules/observability/README.md`
    - `modules/hot-reload/README.md`
    - `modules/testing/README.md`
11. `docs/architecture/LumioGameEngine_Architecture_v1.0.md`
12. 额外读取：
    - `docs/architecture/.baseline.sha256`
    - `.spec/decisions/README.md`
    - `.spec/tasks/README.md`

Runtime 的公开 baseline manifest 指向 v1.0 镜像，但本次没有执行 checksum 命令，因此不能声称校验通过。

### 12.3 实际读取的公共架构源

读取了：

- 根 `README.md`
- `docs/architecture/LumioGameEngine_Architecture_v1.0.md`
- `docs/architecture/ADR_INDEX.md`
- `docs/architecture/DECISIONS_PENDING.md`
- `docs/adr/README.md`
- 权威 `.spec/decisions/ADR-001` 至 `ADR-016`
- `schemas/index.json`
- `schemas/common.schema.json`
- Schema Registry 中全部登记 Schema
- `fixtures/index.json`
- `fixtures/valid/` 与 `fixtures/invalid/` 中全部 43 个登记 Fixture
- `ids/index.json`
- `tools/lumio_contract.py`

### 12.4 只读命令验证状态

| 命令 | 状态 | 说明 |
|---|---|---|
| `git status --short --branch` | 未运行 | 无法访问用户本地 checkout |
| `git show --stat --oneline HEAD` | 未运行 | 本地 HEAD 未知 |
| `git diff --check HEAD^ HEAD` | 未运行 | 本地提交历史未验证 |
| `node .spec/tools/spec-lint.mjs` | 未运行 | 未取得本地执行环境 |
| `node --test .spec/tools/spec-lint.test.mjs` | 未运行 | 未取得本地执行环境 |
| `sha256sum -c docs/architecture/.baseline.sha256` | 未运行 | 只读取了 manifest，不宣称通过 |
| `python3 tools/lumio_contract.py validate` | 未运行 | 已审查 Validator 源码及全部登记 Fixture，但不宣称实际命令成功 |

因此，本报告只对所读取文档和契约内容作架构判断；Git 状态、Lint、Checksum 和 Contract CLI 的实际退出码均为“待验证”。

### 12.5 事实、推断和 Decision Gap

- **事实**：文档、Schema、Fixture、Validator 之间可直接观察到的声明或冲突。
- **推断**：基于声明推导出的实现风险，例如字段原地写入后发生异常可能留下部分状态；报告中均明确标注为风险，而非声称代码已经发生该缺陷。
- **Decision Gap**：文档没有给出足以唯一实现的决策，不等同于已发生的代码 Bug。
- **待验证**：依赖本地工作区、真实 C# 实现、命令执行或 Benchmark 才能确认的事项。

### 12.6 `RT-D-001` 至 `RT-D-011` 状态

本仓尚无本地 ADR，以下决策门均仍为未批准状态。

| 决策门 | 当前判断 | 与本报告关系 |
|---|---|---|
| RT-D-001 Assembly 映射 | 未批准，当前阻塞 | DAG 缺边、Facade/Port 类型归属必须先明确 |
| RT-D-002 ECS Storage | 可继续保持未决 | 不阻塞语义，但不能影响稳定 View 和 Tick 原子性 |
| RT-D-003 Command 冲突/容量 | 被 P0 阻塞 | 必须先建立 Prepared Apply |
| RT-D-004 Journal/Reservation | 被 P1 阻塞 | Txn 状态与 durable record 未闭合 |
| RT-D-005 History/Tombstone | 未批准 | 需统一保留窗口与 Voxel Apply |
| RT-D-006 GAS 投影/求值 | 被 P1 阻塞 | 通用状态机未冻结 |
| RT-D-007 Persistence 后端/耐久 | 未批准 | 后端可后置，Record Envelope 不能后置 |
| RT-D-008 Config Compiler/Reader | 被 P1/P2 阻塞 | Typed validation 与编译器所有权未清 |
| RT-D-009 Observability Sink/背压 | 未批准 | Sink 可后置，Durable Journal 语义不可依赖普通日志 |
| RT-D-010 Scope 超时/Root 验证 | 被 P1 阻塞 | 原子新旧 Scope 切换未定义 |
| RT-D-011 Reference Host 保真度 | 未批准，Foundation 阻塞 | 无法证明 Replay、LocalEmbedded 与真实 Host 的差异边界 |

### 12.7 仍需明确回答的架构问题

1. Processor 对已有字段的写入是原地写、Overlay 写，还是带 Undo Journal 的写？
2. 当前 Tick 的唯一权威 Commit Point 是哪个 Phase 的哪个操作？
3. ECS PreparedDelta 如何保证在 Voxel Commit 之后不再业务拒绝？
4. Voxel Replica Apply Port 与 Voxel Snapshot Provider 的具体所有者和版本契约是什么？
5. Indeterminate 是否允许参与者状态为 Unknown，而不仅是 Boolean marker？
6. FullSnapshot/Delta/Prediction Confirm 的 typed body Schema 位于何处？
7. GAS 通用 Ability/Effect 状态集合和转移表由谁冻结？
8. Config 编译发生在离线 Toolchain、开发 Host，还是稳定 Runtime？
9. 新 Gameplay Scope 在何时完成验证并原子替代旧 Scope？
10. 首个 Snapshot 前发生故障时，FailureBundle 如何保持 Schema 合法和可重建？

---

## 附录：基线说明

历史文件 `LumioGameEngine_Architecture_v0.3.md` 仅为兼容性指针。规范性架构基线为：

- **ArchitectureBaselineId**：`LGE-V1.0-2026-08-27`
- **规范性文档**：`LumioGameEngine_Architecture_v1.0.md`
- **公共架构源**：`LumioGameEngineArchitecture`
- **约束**：不得在 v0.3 兼容指针中新增架构决策。
