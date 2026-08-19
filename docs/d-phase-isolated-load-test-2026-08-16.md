# D 阶段隔离负载测试报告（2026-08-16）

## 结论

本轮结果为 **FAIL**。本地持久化入口可以稳定接收 3,000 Tick/s，但 CollectorGateway 到 Redis 的转发能力无法持续跟上，不能满足 D 阶段“持续吞吐不低于 3,000 Tick/s、Redis ACK P95 小于 5 秒”的门槛。

## 测试环境

- 本地运行当前源码构建的 API、Worker、StrategyScanner 和 CollectorGateway。
- 云端使用独立 MySQL/Redis 容器、独立端口和独立 Docker volume。
- 本地通过临时 SSH 隧道访问测试容器，未修改公网安全组。
- 测试数据只使用 `sim:dphase-20260816-load:*` 事件和 `LOAD.*` 证券代码。
- Python 未连接 MySQL 或 Redis；模拟数据通过 CollectorGateway named pipe 进入系统。

## 10 分钟连续负载

| 指标 | 结果 |
|---|---:|
| 目标速率 | 3,000 Tick/s |
| 持续时间 | 600 秒 |
| 发送 Tick | 1,785,000 |
| named pipe durable Tick | 1,785,000 |
| named pipe 失败 Tick | 0 |
| named pipe durable P95 | 8.5061 ms |
| 结束后 Gateway pending 源 batch | 约 3,000 |
| 清理前 Redis V3 Stream key | 64 |
| 清理前 Redis V3 Stream entry | 10,000 |
| Redis 内存 | 7.97 MiB / 512 MiB |

named pipe durable 只代表本地 inbox 已落盘，不等于 Redis ACK。pending 在测试期间持续增长，因此不能使用 8.5 ms 作为云端 ACK 延迟。

## 故障注入

| 故障 | 观察结果 |
|---|---|
| CollectorGateway 终止 5 秒并重启 | pending 保持；重启后继续下降 |
| API 终止 5 秒并重启 | pending 保持；API 恢复后继续下降 |
| 隔离 Redis 中断 5 秒 | 故障期间没有删除 pending；恢复后继续转发 |
| 隔离 MySQL 中断 5 秒 | 控制面暂停；Tick pending 没有静默删除 |

本轮未执行真实 GM SDK 进程崩溃、官方 K 线 20,000 条闭环、MySQL 人工死锁和任务超时验收。容量门槛已经失败，继续扩大故障矩阵不会改变本轮结论。

## 测试中发现并修复

1. Gateway 原主循环串行等待 History/Snapshot Python 进程，导致心跳、命令轮询、Bar 和 Tick 转发互相阻塞；已拆成独立循环并增加 6 个采集并发槽。
2. Tick batch 原先固定发送 `shard_id=0`，多证券 batch 被 API 全量拒绝；已按中国交易日和 FNV-1a 64 分片稳定拆批。
3. 已完成的 Tick assignment 在 worker 后续崩溃时不能转为 failed；已允许 Tick command 从 completed 转 failed，并允许失败版本重新下发。
4. 已 ACK inbox 文件原先改名为 `.completed.json` 并永久累积；已改为确认后删除。
5. History worker 重定向 stdout 但不读取，存在管道缓冲区死锁风险；已并发读取 stdout/stderr。

## 阻断项

1. Gateway 对拆分后的 Tick 分区逐个新建 gRPC 连接并串行等待 ACK，是当前主要吞吐瓶颈。
2. 仓库全量迁移不能从空库顺序重放：早期脚本写死数据库名，且历史迁移之间存在重复列；本轮只能克隆正式库无数据 schema 后补 025/026。
3. 正式云库 schema 尚未包含 025/026 CollectorGateway 控制平面表，当前源码不能直接使用该正式 schema。
4. 目前没有正式的 V3 测试驱动来直接记录 Redis ACK 分位数和逐事件数量守恒。

## 清理与恢复

- 两台云主机的 D 测试容器和 Docker volume 已删除并复查无残留。
- 本地测试 inbox、日志、生成器摘要和 SSH 隧道已删除。
- 三项 Windows 服务已恢复为 `Running / Automatic`。
- 六项 AStockMonitor 计划任务已恢复为启用状态。
- 正式 MySQL/Redis 容器和数据未执行删除、迁移或测试写入。
