---
name: testing
description: 测试与验收——测试分层政策、TDD 时机、验收 DoD 与验证证据;实现功能/修 bug 时查
metadata:
  type: doc
  status: 已交付
---

# 测试与验收（含 TDD 政策）

> 本文定**政策**（测什么、何时测、怎么算过）；“先写失败测试再实现”的**方法**在技能 [`skills/test-driven-development`](../../skills/test-driven-development/SKILL.md)。

## 测试分层（通用政策）

- **单元测试**：默认层，随项目验证命令（`AGENTS.md`「收口门槛」）每次跑，快、无外部依赖。
- **集成测试**（真库 / 真服务）：显式触发，不进默认验证命令，保持收口快。
- **端到端 / E2E**：显式触发；关键主链路至少一条。

## 何时走 TDD

- 必须走：新功能、修 bug（先写能复现的失败测试，修完留作回归测试）、改无测试保护的关键逻辑。
- 可不走：纯文档改动、一次性脚本。豁免在交回物里声明。
- 写测试、加 mock、想给生产类加 test-only 方法前，先查反模式清单：[`testing-anti-patterns.md`](../../skills/test-driven-development/testing-anti-patterns.md)——测 mock 行为、test-only 方法入生产、不理解依赖就 mock、不完整 mock，一律禁止。

## 验证证据

形式要求以 `AGENTS.md`「交回物格式」为单一权威——「已通过」三个字不是证据。

## 验收标准（Definition of Done）

- [ ] 收口门槛命令全绿（当前至少执行 `node .spec/tools/spec-lint.mjs` 与 `node --test .spec/tools/spec-lint.test.mjs`）。
- [ ] 新增 / 修改行为有测试覆盖；bug 修复留有回归测试。
- [ ] 无 lint / 类型错误、无调试残留。
- [ ] 相关知识文档已更新（见 [`workflow.md`](./workflow.md)）。

## 项目测试栈与命令

当前仓库尚未提交 Runtime 实现工程；现阶段默认验证为：

```text
node .spec/tools/spec-lint.mjs
node --test .spec/tools/spec-lint.test.mjs
```

首次引入 .NET 工程时，必须加入 formatter/analyzer、单元/Property/Golden、Reference Host、Replay/Differential 与 Hot Reload Soak 命令。公共 Runtime Contract 变更还必须在架构源安装依赖并运行 `python3 tools/lumio_contract.py validate`。

## 本仓 Headless / 契约测试面

- ECS/Query/CommandBuffer/Processor/Determinism/Revision/Entity Property 和 Golden Test。
- CrossWorldTxn 的 Duplicate、Timeout、Conflict、Lost Result、Crash Fixture。
- GAS 生命周期、PredictionFrame、Authority Confirm、Rollback、Snapshot/Hash。
- ReferenceVoxelPort 与 Native Voxel Differential、Local Transport Fidelity、Replay 首差异。
- Hot Reload 100 次 Soak、ALC/Task/Timer/Handle 泄漏和异常隔离。
- 日志背压、配置优先级、Snapshot/WAL 恢复和 Failure Bundle 重建。
- Tick/Processor、Revision、Txn、Replication、Mapping 或 Snapshot 语义变化必须带状态机、正向/失败 Fixture 与 SchemaEpoch 说明。
