# Binding 公开面（宿主消费）

`Lumio.GameRuntime.Replication.Binding` 是连接绑定、`NetEntityId` 发号与 Attribute Query 的唯一 Runtime 实现。宿主只转发，不得另建绑定表或查询 switch。目标框架：`netstandard2.1` 与 `net10.0`。

语义真值：架构仓 `engine/wire/entity-binding-and-query-v1.json`（C-2′）。失败分两类：结局类 `{outcome}`（`non_existent` / `stale_generation` / `invisible` / `unauthorized` / `tombstoned`，不带 `code`）；请求形 `{outcome: request_error, code, detail}`。`undeclared_attribute` 不得映射为 `unauthorized`。

## 绑定记录

`ConnectionBinding` 只含五元组：`AccountId`、`RoomId`、`NetEntityId`、`EntityType`、`ConnectionGeneration`。会话号与宿主句柄不是绑定字段；请求携带 `SessionId` / `HostHandle` / `HostPointer` / `AccountEntityRef` / `StorageHandle` 一律 `invalid_binding_shape`。

## 发号与恢复

| API | 用途 |
| --- | --- |
| `Admit(connection, accountId, roomId, entityType)` | 准入。Runtime 身份表分配永不复用的 128-bit `NetEntityId`（小写 32 位 hex），登记墓碑空间。宿主不得传入 `NetEntityId`。 |
| `Admit(AdmitRequest)` | 同上；`NetEntityId` 或违禁字段非空则 `invalid_binding_shape`。 |
| `Bind(connection, BindingRecordRequest)` | **仅恢复** Runtime 已发号。任何未登记号（含 `"101"`、`"host-minted-N1"`、自铸 hex）→ `invalid_binding_shape`；墓碑/退役号 → `tombstoned`。已有其他连接或另一账号占用同一号时拒绝。宿主不得用 Bind 准入。 |
| `CaptureIdentityTable()` / `Create(IdentityTableSnapshot)` | 把已发号与墓碑读回下一进程，保证跨进程不复用。 |

`EntityIdentity.accountId` 不在声明表；查询返回 `undeclared_attribute`。

## 生命周期

| API | 语义 |
| --- | --- |
| `Disconnect(connection)` | 去掉活跃绑定；实体在断线窗内保持 Room 可见。随后 `SelfLookup` / `ResolveByConnection` 为 `binding_not_found`。 |
| `Rebind(toConnection, accountId, roomId, RebindMode.Reconnect)` | 重连：继承同一 `NetEntityId`，`connectionGeneration` 严格递增。 |
| `Rebind(toConnection, accountId, roomId, RebindMode.Takeover)` | 顶号：旧连接终止，世代递增。 |
| `Rebind(fromConnection, toConnection)` | 已知双连接时的顶号/重绑。 |
| `Expire(netEntityId)` | 过期销毁 → tombstone，永不复活、永不改指。后继实体必须重新 `Admit` 拿新号。 |
| `ListBindings(roomId)` | 本 Room 当前活跃绑定（不含已 Disconnect 的保留实体）。 |

## 查询

| API | 调用方 |
| --- | --- |
| `SelfLookup(connection, callerScope)` | 客户端自查，`callerScope` 必须是 `client-replica`。 |
| `ResolveByConnection(roomId, connection)` | 服务端，Simulation Owner Thread。 |
| `ResolveByNetEntityId(roomId, netEntityId, connectionGeneration, callerScope)` | 服务端或客户端；带世代则过期为 `stale_generation`。 |
| `QueryAttribute(AttributeQueryRequest, callerConnection?)` | 单实体单已声明 `AttributeId`。分类顺序：存储寻址 → 文法 → 已声明。 |

`Spawn(roomId, entityType)` 由 Runtime 发号。`Spawn(roomId, netEntityId, entityType)` 只接受已发号；宿主传入自铸 id 与 Bind 一样拒绝。

N-10 删除宿主自有绑定表后只调本面，不在 Server/Client 再写一份查询。不得用 `Bind`/`Spawn` 自铸 `NetEntityId`。
