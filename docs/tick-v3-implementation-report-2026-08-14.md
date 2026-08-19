# Tick V3 实施报告

> 日期：2026-08-14  
> 结论：核心链路开发、迁移、部署和 2 分区灰度已完成  
> 当前阶段：交易时段持续观察，尚未扩展到全市场

## 1. 已完成内容

1. Python SQLite Outbox 增加 `pending / acknowledged / rejected / in_flight / expired` 状态、租约令牌、过期时间、批量 ACK 和幂等迁移。
2. Tick 与官方 Bar 使用物理隔离的 WAL 数据库；Tick 最长只重放 120 秒，官方 Bar 永不过期。
3. 东方掘金 SDK 进程只负责订阅、标准化和 Outbox 写入；每个分区新增独立 Relay 进程负责 gRPC 传输。
4. Tick 使用按证券代码稳定分片的 `TickBatch`；官方 Bar 使用独立有限批次 RPC，二者互不阻塞。
5. .NET gRPC 接入支持批量校验、批量 ACK 和 64 分片 Redis 原子 Lua 写入。
6. Redis 同一批次原子完成 Stream、最新行情 Hash、元数据、水位和 TTL 更新。
7. 最新行情读取支持单股 O(1) 和跨分片批量 HMGET；MySQL Tick 回退已移除。
8. Retention Worker 按消费组安全水位裁剪，不会删除仍处于 Pending 的消息。
9. MySQL 继续只保存东方掘金官方 5m、30m、60m、1d K 线，不保存 Tick。

## 2. 数据迁移

| 项目 | 结果 |
|---|---:|
| 迁移前 Outbox 总记录 | 732,315 |
| 迁移前待确认记录 | 720,189 |
| 标记过期的旧 Tick | 713,189 |
| 保留并继续投递的官方 Bar | 7,000 |
| 拒绝记录 | 0 |

迁移前完整备份：`.runtime/backups/tick-outbox-20260814-before-v3`。迁移未执行物理删除或 `VACUUM`，可审计、可回退。

## 3. 验证结果

| 验证项 | 结果 |
|---|---|
| Python 测试 | 36 passed，1 个 protobuf 依赖弃用警告 |
| .NET 10 Release 构建 | 0 warnings，0 errors |
| 真实 Python Outbox → gRPC → Redis → Batch ACK | 1,000/1,000 ACK，Pending=0 |
| Redis 幂等重放 | 3 条重放全部判重，不重复追加 |
| 过期 Tick | 5 分钟旧 Tick 被拒绝进入实时流 |
| 单股/批量最新行情接口 | 通过 |
| 单发布器隔离压测 | 约 25,120 Tick/秒 |
| 4 并发发布器隔离压测 | 约 27,295 Tick/秒 |
| 2 分区 SDK/Relay 灰度心跳 | 连续推进，Outbox Pending=0 |

20,000 Tick/秒正式目标已满足；50,000 Tick/秒拉伸目标尚未满足。若未来确实需要 50,000 Tick/秒，应优先横向扩展 Redis 实例/Cluster 与 gRPC Ingest 实例，而不是恢复逐条写入。

## 4. 灰度运行状态

- `AStockMonitor.Api`：Running。
- `AStockMonitor.Worker`：Running。
- `AStockMonitor.StrategyScanner`：Stopped，本次未恢复策略扫描。
- `AStockMonitor-MarketCollector`：Running，固定 2 个分区，每分区 50 只股票。
- 每个分区实际包含 1 个 SDK 进程和 1 个 Relay 进程，共 4 个子进程。
- Redis/MySQL 容器保持健康运行。
- 东方掘金当前导出的最近交易日为 2026-08-13；本次灰度期间没有产生新的 Tick 回调，因此实时数据正确性和端到端 P95/P99 仍需在下一个有真实 Tick 的交易时段验收。

## 5. 扩容前门槛

满足以下条件后，才把计划任务从 2 分区扩展到全量分区：

1. 真实交易时段连续观察至少一个完整交易日；
2. 每个 Relay 心跳间隔不超过 10 秒；
3. 正常 Outbox 最旧 Tick 年龄小于 2 秒，Pending 不持续增长；
4. SDK 回调到 Redis 的 P95 小于 100ms、P99 小于 300ms；
5. MySQL Tick 新增量始终为 0；
6. 官方 Bar 能持续幂等固化，且不影响 Tick 心跳和延迟；
7. Redis 消费组无持续增长的 Lag/Pending。

扩容时重新注册计划任务，例如：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-market-collector-task.ps1 `
  -Workers 0 -SymbolsPerWorker 50 -MaxSymbolsPerExchange 500
```

`Workers=0` 会按东方掘金当前单会话 50 只证券的限制自动计算进程数。扩容前不要手工同时启动另一套 Supervisor，避免同一 `worker_id` 双写同一 Outbox。
