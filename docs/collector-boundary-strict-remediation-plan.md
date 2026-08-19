# 采集端职责边界严格整改方案

## 1. 目标与不可违反的约束

本方案只接受一条正式数据路径：Python 负责调用东财掘金 GM SDK 采集，.NET 负责全部控制、存储、调度、重试、状态和发布。

```text
Python GM SDK Worker
  -> .NET CollectorGateway（本机可靠接收）
  -> .NET Ingest API（校验与业务写入）
  -> MySQL / Redis / .NET Transactional Outbox
  -> 下游策略与 Web API
```

以下行为在目标版本中一律禁止：

- Python 连接 MySQL、Redis，或读取其账号、密码、地址。
- Python 领取 `market_recovery_item`、推进任务状态、写 K 线、写 checkpoint、写 Outbox。
- Python 轮询 Redis `desired` 集合决定热点订阅。
- Python 维护 SQLite Outbox、Relay、业务重试、优先级仲裁或数据过期策略。
- 旧路径与新路径同时向生产 Redis、MySQL、Outbox 写入。
- 任何异常时自动回退到 Python 直写 MySQL/Redis 的旧路径。

故障处理的原则是“停止新任务派发并保留 .NET 任务/收件箱状态”，而不是启用旧路径兜底。

## 2. 当前功能与整改影响

| 现有功能 | 当前实现 | 严格整改后的实现 | 功能影响与必须修复项 |
|---|---|---|---|
| 全市场快照 | Python 读 MySQL 股票池、读 Redis 热点池、每 5 秒 `current()` | .NET 发出快照命令；Python 仅执行 `current()` 并回传 | 股票池、交易日、热点排除、陈旧判断迁入 .NET；Python 不再有 MySQL/Redis 配置 |
| 热点 Tick | .NET 写 Redis desired；Python Supervisor 轮询并启停 SDK/Relay | .NET 持久化分配并通过命令流下发给 0-6 个 Tick Worker | 删除 Redis 轮询、Supervisor、Relay；版本号和 ACK 保证旧订阅不会覆盖新订阅 |
| 官方 K 线补数 | .NET 建任务；Python 领取 MySQL 任务并直接写 K线 | .NET 领取并派发任务；Python 下载 GM history；.NET 批量落库 | 删除 Python `recover --watch`、Python `upsert_bars`、Python checkpoint 和 Python 事件发布 |
| K 线落库 | Python 批量 SQL 与 C# `CanonicalBarWriter` 两套逻辑并存 | 仅 `.NET CanonicalBarBatchWriter` | 统一 revision、row_hash、checkpoint、bar_event_outbox 和死锁重试；彻底删除双写入口 |
| Tick 持久化 | Python SQLite Outbox 后 gRPC；积压超时后丢弃 | .NET Gateway 本机收件箱后 .NET Ingest | 队列、ACK、过期和背压由 .NET 定义；禁止 Python 静默丢弃或自行 ACK |
| Bar Event 发布 | MySQL Outbox -> .NET Publisher -> Redis | 保持不变 | 写入来源改为唯一的 .NET 批量 K线写入器 |
| 策略重算 | 仅旧恢复运行会触发；`official-v4-*` 被排除 | K线修订后由 .NET 显式创建策略重算任务 | 修复正式官方 K线补齐后未触发策略重算的问题 |
| 运行状态 | 快照、热点、gRPC、任务状态混杂 | 按命令、采集、Gateway、Ingest、落库、发布、策略分层 | Web API 必须显示每层水位，不能以 Python 心跳代替落库成功 |
| 凭据 | Python 同时具备 GM、MySQL、Redis 凭据 | Python 只保留 GM Token 和本机 Gateway 身份 | MySQL/Redis 凭据仅由 .NET 服务通过受保护配置读取 |

## 3. 目标组件与职责

### 3.1 .NET Worker：唯一控制面

`.NET Worker` 负责：

- 交易日、开收盘、K 线边界和快照节奏。
- 热点池计算、持久化分配版本和 Tick Worker 容量管理。
- 官方 K 线缺口检测、任务领取、租约、重试和取消。
- 采集命令派发、超时判定和重新派发。
- 官方 K 线完成后的策略重算任务创建。

它不调用 GM SDK，也不依赖 Python 轮询共享数据库或 Redis。

### 3.2 .NET CollectorGateway：本机接入面

新增 `AStockMonitor.CollectorGateway`，运行在东财终端所在的交互式 Windows 用户会话中。它是 .NET 程序，不承担业务调度。

职责：

- 按 .NET Worker 的命令启动、停止和监控 Python SDK Worker 子进程。
- 通过本机命名管道接收 Python 原始数据及进程健康状态。
- 为每个批次生成/校验 `command_id`、`batch_id`、`worker_id`、`sequence`。
- 将已接收数据写入 .NET 所有的本机耐久收件箱，再向 Python ACK。
- 以 gRPC 将收件箱数据发送给 Web API，收到 API 业务 ACK 后删除本机收件箱记录。
- 上报 Worker 进程、命令版本、收件箱积压和最后成功 ACK 时间。

Gateway 与 Python 的本机收件箱归 .NET 所有。Python 只有有限内存缓冲，不能持久化业务队列。

由于东财终端依赖交互式会话，Gateway 可以由一个仅负责进程存活的 Windows 计划任务启动，但该任务不包含交易日、热点、补数或重试逻辑。现有三个 Python 采集计划任务必须删除。

### 3.3 Python GM SDK Worker：纯采集适配器

Python 只保留三个可执行角色：

| Worker | 数量 | 输入 | 输出 |
|---|---:|---|---|
| `snapshot-worker` | 1 | `SnapshotRequest` | 原始快照批次 |
| `tick-worker` | 0-6 | `TickSubscriptionAssignment` | 原始 Tick 批次 |
| `history-worker` | 0-6，按任务启动 | `HistoryCollectionRequest` | 原始官方 K线批次 |

Python 只通过本机命名管道连接 Gateway。它不拥有网络数据库连接，也不理解 K 线 revision、策略、热点计算、交易日或重试策略。

### 3.4 .NET Web API / Ingest：唯一数据写入面

Web API 的 gRPC Ingest 只接受来自受认证 Gateway 的数据。其内部按三类流物理隔离：

- Tick Ingest：Redis Tick Stream、最新行情投影、来源优先级和去重。
- Snapshot Ingest：快照覆盖率、陈旧判断、来源优先级和去重。
- Official Bar Ingest：K线结果收件箱、批量 canonical 写入、checkpoint 和事件 Outbox。

三类流不能共享无上限队列，也不能因为 Bar/MySQL 延迟阻塞 Tick ACK。

## 4. 命令与确认协议

现有 `AssignmentCommand` 只保留为兼容参考，正式协议必须替换为下列明确消息。

```text
WorkerRegister(worker_id, role, sdk_version, capacity)
TickSubscriptionAssignment(command_id, worker_id, assignment_version, symbols)
SnapshotRequest(command_id, universe_version, symbols, fields, deadline)
HistoryCollectionRequest(command_id, recovery_item_id, symbols, frequency, start, end)
CancelCommand(command_id, reason)

RawTickBatch(command_id, batch_id, worker_id, sequence_range, payload)
RawSnapshotBatch(command_id, batch_id, universe_version, payload)
RawBarBatch(command_id, batch_id, recovery_item_id, payload)
CollectionCompleted(command_id, counts, source_watermark)
CollectionFailed(command_id, error_code, message)
```

确认语义必须固定：

| 数据 | Python 可释放内存的 ACK | .NET 最终成功标准 |
|---|---|---|
| Tick | Gateway 已耐久写入 .NET Tick 收件箱 | Redis 原子 Stream/最新行情写入完成 |
| Snapshot | Gateway 已耐久写入 .NET Snapshot 收件箱 | Redis 投影完成，覆盖率已记录 |
| 官方 K线批次 | Gateway 已耐久写入 .NET Bar 收件箱 | K线、checkpoint、bar_event_outbox、任务进度同事务提交 |
| 历史任务完成 | .NET 返回 `JobCompleted` | 所有批次已应用，策略重算任务已创建 |

严禁将“收到 gRPC 消息”“Python 写入本地文件”“Redis 客户端已连接”显示为采集完成。

## 5. 官方 K线的唯一写入流程

这是本次整改的核心，必须完全替换 Python 直写和单条 `CanonicalBarWriter` 两条旧路。

```text
.NET Worker 创建或领取 market_recovery_item
  -> 派发 HistoryCollectionRequest
  -> Python history-worker 调用 GM SDK
  -> Gateway 耐久写入 collector_bar_inbox
  -> API 批量校验并写入 official_bar_staging
  -> CanonicalBarBatchWriter 按固定顺序应用
  -> K线 + checkpoint + bar_event_outbox + recovery_item 进度同事务提交
  -> 创建 strategy_replay_task
  -> BarEventOutboxPublisher 发布 Redis Bar Stream
```

### 5.1 数据库状态

保留 `market_recovery_item` 作为官方 K线任务唯一来源，不新建第二个权威任务队列。扩展其状态为：

```text
planned -> leased -> dispatched -> collecting -> received -> applying -> completed
                                                   |                         |
                                                   +------ failed <-----------+
```

增加以下 .NET 所有的表：

- `collector_command`：命令、版本、目标 Worker、超时和派发状态。
- `collector_result_batch`：Gateway/API 收到的批次，唯一键为 `(command_id, batch_id)`。
- `official_bar_staging`：已确认接收但尚未 canonical 应用的官方 K线。
- `strategy_replay_task`：受影响股票和日期区间的策略重算任务。

### 5.2 CanonicalBarBatchWriter

新增批量 Writer，不允许再对每根 K线执行 `SELECT ... FOR UPDATE` 后插入。

- 同一事务内按 `frequency, trading_date, symbol, eob` 固定排序。
- 采用幂等 UPSERT；revision 根据持久行和 `row_hash` 统一计算。
- 同一事务更新 K线、`bar_sync_checkpoint`、`bar_event_outbox`、`market_recovery_item`。
- MySQL 1205/1213 仅在 .NET 事务层做带抖动的快速重试，最多 5 次；重试耗尽时任务保持可诊断失败，不转给 Python 直写。
- 已应用批次的唯一键永久保留到任务审计期结束，保证重复上报不重复修订。

## 6. Tick 与快照的严格处理

### 6.1 热点 Tick

- `.NET Worker` 在 MySQL 保存热点池和每个 Tick Worker 的分配版本。
- Gateway 向已注册的 Tick Worker 下发分配；Python 收到新版本后先确认 SDK 订阅成功，再返回 `AssignmentApplied`。
- .NET 只在收到 `AssignmentApplied` 后将该 Worker 标记为在线；不再以 Redis desired 代替实际订阅成功。
- 快照排除条件改为“.NET 确认在线且订阅成功的热点股票”，不是 desired 股票。

### 6.2 全市场快照

- `.NET Worker` 从 MySQL 的 `instrument_daily_status` 取得股票池和交易日判断。
- 每次请求带 `universe_version`；Python 只执行 GM `current()`。
- .NET 记录请求股票数、返回数、陈旧数、有效数和最终 Redis ACK 数。
- 任一请求失败必须形成可见失败事件和下一次调度记录，不能把旧快照继续标为正常。

### 6.3 Tick 过载

不再把超过固定时间窗的 Tick 自动标记为“正常过期”。.NET Gateway/Ingress 必须：

- 分别记录源接收、Gateway 收件箱、API ACK、Redis ACK 的水位与最老年龄。
- 超过 SLO 时进入 `degraded`，停止扩张热点订阅并告警。
- 只有经过明确的交易时效策略批准后才允许丢弃；丢弃必须计为数据事件和健康失败，不能算成功 ACK。

## 7. 策略重算影响

现有 `RecoveryStrategyReplayWorker` 排除了 `official-v4-*`，导致正式官方 K线修订后可能没有重算策略。

整改后由 CanonicalBarBatchWriter 在同一事务中创建 `strategy_replay_task`：

- 范围是受影响股票和修订 K线区间。
- 策略服务按“前 45 天预热 + 修订区间 + 后 5 天”读取正式 K线。
- 只重建对应股票和区间的策略事件、命中和生命周期。
- 回放运行模式为 `recovery-replay`，禁止发送盘中通知或交易指令。
- K线任务只有在 Bar 已落库、Bar Event Outbox 已创建、策略任务已创建后才能进入 `completed`；策略任务自身有独立状态，不阻塞 Bar Event 发布。

## 8. 实施阶段

### 阶段 A：先决条件与冻结

1. 冻结 `snapshot_worker`、`hot_tick_supervisor`、`recovery.py` 的新增功能。
2. 记录当前 Python 进程、任务表状态、Outbox 积压、Redis 水位、K线 checkpoint。
3. 在测试环境复制云 MySQL/Redis 的结构和脱敏数据。
4. 新增 .NET 受保护凭据提供器；Python 运行环境删除 MySQL/Redis 配置项。
5. 建立 `CollectorGateway` 项目、命名管道协议和 gRPC 认证。

完成标准：测试环境中 Python 进程无法连接 MySQL/Redis，.NET 可读取受保护凭据。

### 阶段 B：命令、收件箱与 K线唯一写入器

1. 实现 `collector_command`、`collector_result_batch`、`official_bar_staging` 和 `strategy_replay_task` 迁移。
2. 实现 .NET 任务租约、命令派发、超时、取消和幂等确认。
3. 实现 Gateway 的本机耐久收件箱和 API 重传。
4. 实现 `CanonicalBarBatchWriter` 与 1205/1213 事务级重试。
5. 删除 OfficialBar 单条 gRPC 写入入口，禁止 Python `upsert_bars` 用于实时/补数任务。

完成标准：以 20,000 根混合频率 K线压测，全部落库、零双写、零未解释死锁、所有 checkpoint/Outbox 一致。

### 阶段 C：Python 采集适配器改造

1. 将 Snapshot Worker 改为只接收命令、调用 `current()`、回传批次。
2. 将 Tick Worker 改为只接收 gRPC 分配、订阅 SDK、回传 Tick；删除 Redis 依赖。
3. 将 History Worker 改为只接收 history 请求、下载、回传 Bar；删除 MySQL 任务领取和写入。
4. 删除 Python SQLite Outbox、Relay、Supervisor、Recovery Manager、MySQL/Redis Python 依赖。

完成标准：Python 静态依赖扫描中不存在 MySQL/Redis 客户端、数据库 SQL、任务状态 SQL 或数据库凭据。

### 阶段 D：端到端集成与故障验证

1. 在隔离环境启动一个 Gateway、1 个 Snapshot、6 个 Tick、6 个按需 History Worker。
2. 验证热点分配更新、全市场快照、官方 K线补数、K线修订、Bar 事件、策略重算。
3. 注入 Gateway 重启、Python Worker 崩溃、API 短断、Redis 短断、MySQL 死锁和任务超时。
4. 验证每种故障只保留 .NET 中可恢复的命令/收件箱/任务状态，绝不启用旧 Python 写入路径。

完成标准：故障后数据可由命令和批次 ID 完整追踪；没有静默完成、静默丢弃或重复修订。

### 阶段 E：生产切换

1. 选择非交易时段，停止旧 Python Snapshot、Hot Tick、Recovery 任务，并等待其已发送数据处理完成。
2. 将仍处于 `planned/replaying` 的 K线任务迁为新 .NET 状态；禁止新旧任务并行领取。
3. 部署 `.NET Worker`、Web API、StrategyScanner、CollectorGateway 和新的纯 Python Worker。
4. 先启动 API、Worker、StrategyScanner、Gateway，再由 Worker 下发 Python 采集命令。
5. 通过验收项后，撤销 Python 运行账户访问 MySQL 3306 和 Redis 6379 的网络权限，删除旧计划任务与旧代码。

切换失败时的唯一处理是停止 `.NET Worker` 继续派发、保留任务和收件箱记录、修复新路径后从相同命令继续；不得恢复 Python 直写 MySQL/Redis。

## 9. 验收门槛

### 功能验收

- 热点 Tick 分配版本、在线确认和快照排除三者一致。
- 快照请求数、返回数、有效数、Redis ACK 数可逐次查询。
- 20,000 根官方 K线全部经历“命令、批次、staging、canonical、checkpoint、outbox、策略任务”完整链路。
- 正式 `official-v4-*` K线修订能创建并完成对应策略重算任务。
- Web API 能分别展示 Tick、Snapshot、Bar 的采集、Gateway、Ingest、落库、发布水位。

### 可靠性与性能验收

- 10 分钟混合交易日压测下，Tick 持续处理能力不低于 3,000 条/秒，P95 Redis ACK 小于 5 秒。
- 不允许将积压 Tick 标为成功过期；任何丢弃必须显式报警并使健康检查失败。
- K线 20,000 条全部在既定排空窗口内完成，MySQL 死锁重试后无遗留 `applying` 状态。
- 任意 Python、Gateway、API 进程被杀死后，.NET 任务和收件箱可恢复，不重复写入。

### 安全验收

- Python 进程只持有 GM Token 和本机 Gateway 身份，不包含 MySQL/Redis 地址或密码。
- 云 MySQL/Redis 仅允许 .NET 服务账户访问；Python 运行账户网络层无 3306/6379 权限。
- 所有生产凭据通过受保护配置读取，不写入源码、Python JSON 或日志。

## 10. 不得删减的改造项

以下项目任何一个未完成，都不能宣布职责边界整改完成：

1. Python MySQL/Redis 访问彻底移除。
2. Python Recovery、SQLite Outbox、Relay、Redis polling Supervisor 全部移除。
3. .NET 命令派发和结果确认闭环完成。
4. 官方 K线只剩 .NET 批量 canonical 写入器。
5. 正式官方 K线修订可触发策略重算。
6. 新旧路径不并行生产写入，且不存在自动回退到旧路径。
