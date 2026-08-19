# 正式 V3 Tick 负载测试记录（2026-08-16）

## 3 分钟硬保留改造后的复测结论

第一次 10 分钟测试的 OOM 结论仍保留在下文，作为改造前基线。随后已经完成并部署以下修复：

- Redis `maxmemory` 从 1.00 GiB 调整为 1.50 GiB，策略保持 `noeviction`；
- `TickStreamRetentionWorker` 每 15 秒直接对 64 个固定分片执行 `XTRIM MINID ~`；
- 移除 `KeyExists`、`StreamGroupInfo` 和 Pending 水位前置检查；
- 单分片裁剪异常只记录该分片，不再中断整轮；
- Tick 超过 3 分钟即允许删除，即使消费者仍有 Pending。

正式链路复测分为两组：

| 场景 | 结果 |
|---|---:|
| 3 分钟、50 Tick/s | 发送 9,000；结束时保留 8,640，最早一段按 3 分钟硬保留被裁剪；源 inbox 已排空 |
| 30 秒严格数量守恒 | 发送 600，Redis 新增 600，数量守恒通过 |
| 命名管道持久化 P95 | 3.285 ms |
| Redis ACK P95 | 1,383.074 ms |
| 冒烟结果 | PASS |
| 测试数据清理 | 600 条 Stream entry、40 个预览 key 已删除；测试 run 剩余 0 条 |

30 秒严格守恒原始结果保存在
`.runtime/formal-smoke-20260816/conservation-results.json`。3 分钟测试因硬保留在测试尾部已经开始生效，不应再用“发送总数等于最终留存总数”作为断言；该场景的正确断言是 inbox 排空、ACK 完成、最老消息不突破保留窗口、Stream 不无限增长。

## 结论

本轮 10 分钟、3,000 Tick/s 测试 **FAIL，中途因 Redis OOM 中止**。最新 API、Worker、StrategyScanner 和 CollectorGateway 均能启动，API 健康检查返回 200；失败发生在持续写入超过 Redis `maxmemory` 后，不是进程启动或命名管道耐久写入失败。

## 测试基线

- Redis：公网正式实例，测试前 V3 Tick Stream 为 64 个空分片，0 条 entry，0 条 Pending。
- MySQL：正式 `astock_monitor` 库，仅用于服务健康链路；本轮没有写入测试业务表。
- 测试入口：当前源码发布的 CollectorGateway 命名管道 `astock-monitor-collector-tick`。
- 测试数据：`load:formal-10m-20260816:*`、`LOAD.*`，仅用于本轮压测。
- Gateway/API 凭据：仅注入临时测试进程环境，未写入正式目录或机器级环境变量。

## 烟测

| 指标 | 结果 |
|---|---:|
| 持续时间 | 10 秒 |
| 目标速率 | 1,000 Tick/s |
| 发送 Tick | 10,000 |
| Redis Stream 增量 | 10,000 |
| 数量守恒 | 通过 |
| Redis ACK P95 | 2.08 秒 |
| 源 inbox Pending | 0 |

## 10 分钟测试实际结果

| 指标 | 结果 |
|---|---:|
| 目标速率 | 3,000 Tick/s |
| 目标总量 | 1,800,000 |
| 中止前 Redis Stream entry | 1,069,872 |
| Redis `maxmemory` | 1.00 GiB |
| 中止前 Redis 内存 | 超过 1.00 GiB，触发 OOM |
| Redis 策略 | `noeviction` |
| Redis ACK | `OOM command not allowed`，无法继续 ACK |
| API/Gateway | 进程仍存活，但 gRPC 批次返回异常 |

API 日志中的根因是：

```text
StackExchange.Redis.RedisServerException:
OOM command not allowed when used memory > 'maxmemory'
```

Redis 达到上限后，Gateway inbox 持续累积，说明本地耐久入口仍在工作，但云端 Redis 写入端已拒绝新批次。继续发送会扩大测试数据和磁盘堆积，因此已主动中止。

## 清理核验

- 删除本轮 `md:tick:v3:{20260816:*}:*` 测试 key：256 个。
- 删除测试 Tick Stream entry：1,069,872 条。
- 删除临时 Gateway inbox：剩余 0 个 `.pending.json` 文件。
- Redis 清理后内存：约 310 MiB，低于 `maxmemory`。
- 未删除 MySQL 正式库数据、Bar Stream、策略 Stream 或非本轮 Redis key。

## 当前状态

API、Worker、StrategyScanner 已安装为 Automatic Windows 服务并处于 Running，CollectorGateway 由计划任务启动，API `/health/live` 与 `/health/ready` 均返回 200。正式连接串存放在 Windows 服务注册表的进程环境中，未写入 appsettings 或源码。Redis 容器使用 `unless-stopped` 自动重启。

`MarketCollectionV4Controller.Status` 的 `command_id` 映射问题已经修复并在隔离正式配置端口验证为 200。修复包位于 `.runtime/api-status-fix-publish`；由于本轮 Codex 管理员审批额度耗尽，尚未覆盖正在运行的正式 API 目录，正式端口上的该汇总状态接口仍为 500，其他核心健康、Tick 列表、恢复任务和采集写入接口正常。

## 后续阻断项

在再次执行 3,000 Tick/s 长测前，必须先处理 Redis 容量与背压：确认可靠 Tick Stream 的保留/裁剪在消费者滞后时仍有硬边界，降低单条 payload 的内存占用，并让 Gateway 在 Redis OOM 或 ACK 延迟超阈值时停止接收/限速。当前数据不能作为容量通过结论。
