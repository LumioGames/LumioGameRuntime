# 0001 · Config 编译器归 Game/Toolchain,Runtime 仅验证生成物并在 Tick 边界激活

- 日期:2026-08-27
- 状态:生效

## 背景

架构审查 RT-AR-018 指出 Config 编译器所有权模糊:Runtime `config` 模块既接收人类可读源文件,又暴露 `compile` 候选接口;而公共架构把 Config Table 的 owner 定义为 Game,仓库地图也把具体 Config/Content 放在 Game。不裁决则 Runtime 会逐渐吸收文件格式、默认值、内容引用解析和开发文件监听,形成对 Game 内容与文件系统的隐性依赖。

## 决策

- Game/Toolchain 拥有配置源文件、默认值、编译生成与内容引用解析(编译器)。
- Runtime `config` 只做生成物验证、层级合并、Staging、签名检查、Tick 边界激活与 typed Reader;候选接口不含 `compile`。
- 开发热载经独立 Dev Capability/Adapter 接入;若开发流程必须在 Runtime 进程内编译,该编译只能由 Dev Capability Adapter 承载,不进入稳定 Runtime 接口。

## 后果

- Runtime 对配置源文件与文件系统无隐性依赖,稳定 API 面更小;代价是开发迭代路径依赖 Toolchain/Dev Adapter 的可用性。
- 配置产物格式演进由 Game/Toolchain 主导,Runtime 侧只随架构源 `config-table` 生成物 Schema 升级;`RT-D-008` 的待决范围相应缩小为 Reader 实现栈与 Dev Adapter 接入方式。
