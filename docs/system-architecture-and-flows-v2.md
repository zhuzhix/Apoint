# A股监控程序：当前系统架构与完整功能流程图

> 架构基线：V2  
> 更新时间：2026-08-14  
> 技术栈：Windows、Docker Desktop/WSL2、.NET 10、Python、东方财富掘金 SDK、MySQL 8.4、Redis 8、Grafana、Prometheus、Loki、Tempo、OpenTelemetry。  
> 系统边界：只获取和处理行情、生成研究信号；不连接交易账户、不执行策略交易、不调用下单接口。

本文档描述当前代码已经采用的架构。旧文档中的“Tick 写 MySQL”“Tick 聚合为正式 5/30/60 分钟和日线”“1 分钟正式落库”均已退出生产路径。

## 1. 架构结论

当前系统采用“Windows 行情边缘节点 + .NET 服务域 + Redis 实时层 + MySQL 事实层 + Docker 可观测平台”的结构。

五条不可破坏的架构约束：

1. 东方掘金 SDK 是正式 `5m/30m/60m/1d` K 线的权威来源。
2. Tick 只进入本地短期 Outbox、内存和 Redis，不写 MySQL，每个交易日自然失效。
3. MySQL 只保存正式四周期 K 线、任务状态、审计记录和业务结果。
4. Redis 是可重建的实时状态与可靠消息层，不能替代 MySQL 最终事实。
5. 对子顶底、8 个策略和浏览器推送通过独立 Consumer Group 消费同一套 V2 Bar 事件，互不抢消息、互不阻塞。

```mermaid
flowchart LR
    SOURCE["东方财富掘金终端与 SDK"]

    subgraph EDGE["Windows 行情边缘域"]
        SUP["Python Supervisor"]
        LIVE["实时行情进程<br/>目标约100只股票/进程"]
        SQLITE["每进程独立 SQLite Outbox"]
        HISTORY["历史下载与缺口恢复<br/>多进程 + 检查点"]
    end

    subgraph DOTNET[".NET 10 服务域"]
        API["AStockMonitor.Api<br/>gRPC / REST / Swagger / SignalR"]
        WORKER["AStockMonitor.Worker<br/>预览、事件发布、缺口扫描"]
        SCANNER["AStockMonitor.StrategyScanner<br/>对子与8策略"]
    end

    subgraph REDIS["Redis 实时层"]
        TICK["当日 Tick 快照与 Streams"]
        PREVIEW["当日 1m 盘中预览"]
        BAREVENT["V2 Bar Event Streams"]
        BIZEVENT["对子与策略事件 Streams"]
    end

    subgraph MYSQL["MySQL 最终事实层"]
        BARS["官方 5m / 30m / 60m / 1d"]
        CONTROL["股票池、检查点、质量、缺口、Outbox"]
        RESULT["对子、策略、回放与校准结果"]
    end

    subgraph OPS["访问与运维域"]
        BROWSER["浏览器"]
        OBS["Grafana / Prometheus / Loki / Tempo / OTel"]
    end

    SOURCE --> SUP --> LIVE --> SQLITE --> API
    SOURCE --> HISTORY --> BARS
    API --> TICK
    API --> BARS
    BARS --> CONTROL
    CONTROL --> WORKER --> BAREVENT
    TICK --> WORKER --> PREVIEW
    BAREVENT --> SCANNER --> RESULT
    SCANNER --> BIZEVENT --> API
    BAREVENT --> API
    BROWSER --> API
    BROWSER --> OBS
    API --> OBS
    WORKER --> OBS
    SCANNER --> OBS
```

## 2. 物理部署架构

东方掘金终端和 Python SDK 必须保留在 Windows。当前 MySQL、Redis 和监控栈由 Docker Desktop/WSL2 承载；三个 .NET 进程作为 Windows Service 发布。未来可将 .NET、MySQL、Redis 和监控栈迁往 Linux，Windows 只保留采集节点。

```mermaid
flowchart TB
    subgraph WIN["Windows 主机"]
        GM["东方财富掘金终端"]
        PY["Python 实时采集、历史下载、Recovery"]
        API["AStockMonitor.Api<br/>HTTP 127.0.0.1:5222<br/>gRPC 127.0.0.1:7000"]
        WORKER["AStockMonitor.Worker"]
        SCANNER["AStockMonitor.StrategyScanner"]
        TASKS["Windows 计划任务<br/>实时采集、每日增量、缺口恢复"]
        GM --> PY
        TASKS --> PY
        PY --> API
    end

    subgraph DOCKER["Docker Desktop / WSL2"]
        MYSQL[("MySQL 8.4<br/>127.0.0.1:3306")]
        REDIS[("Redis 8<br/>127.0.0.1:6379")]
        OTEL["OTel Collector<br/>4317 / 4318"]
        PROM["Prometheus<br/>9090"]
        LOKI["Loki<br/>3100"]
        TEMPO["Tempo<br/>3200"]
        GRAFANA["Grafana 中文界面<br/>3000"]
        EXPORTER["MySQL / Redis / Blackbox Exporter"]
        EXPORTER --> PROM
        OTEL --> PROM
        OTEL --> LOKI
        OTEL --> TEMPO
        PROM --> GRAFANA
        LOKI --> GRAFANA
        TEMPO --> GRAFANA
    end

    API --> MYSQL
    API --> REDIS
    WORKER --> MYSQL
    WORKER --> REDIS
    SCANNER --> MYSQL
    SCANNER --> REDIS
    API --> OTEL
    WORKER --> OTEL
    SCANNER --> OTEL
    BROWSER["本机浏览器"] --> API
    BROWSER --> GRAFANA
```

### 2.1 可扩展部署

```mermaid
flowchart LR
    WIN["Windows 行情节点<br/>终端 + SDK + Python"] -->|"gRPC 内网"| APP["Linux/Windows 应用节点<br/>API + Worker + Scanner"]
    APP -->|"TCP 内网"| DATA["数据节点<br/>MySQL + Redis"]
    WIN -->|"OTLP / 主机指标"| MON["监控节点<br/>Grafana 可观测栈"]
    APP --> MON
    DATA --> MON
```

拆分部署时应通过防火墙只开放节点间所需端口；`5222`、`7000`、`3306`、`6379` 不应暴露到公网。

## 3. 服务职责

| 服务/进程 | 核心职责 | 不负责的事情 |
|---|---|---|
| Python Supervisor | 读取严格股票池、分片、启动和重启实时采集进程 | 不计算策略、不下单 |
| Python Live Worker | 同时订阅 Tick 与官方四周期 K 线，写独立 SQLite Outbox，通过 gRPC 发送 | 不写 MySQL Tick |
| Python History | 官方 K 线历史增量、60 日回填、断点续传、质量任务 | 不生成正式 Tick/1m |
| Python Recovery | 领取缺口任务，调用 SDK 补官方四周期 K 线 | 不自行发明 K 线 |
| AStockMonitor.Api | gRPC 接入、Canonical Bar Writer、行情查询、Swagger、SignalR、健康检查 | 不执行全市场策略扫描 |
| AStockMonitor.Worker | Tick 保留、Redis 1m 预览、Bar Outbox 发布、四类缺口扫描、补数后策略重放协调 | 不从 Tick 生成正式 K 线 |
| AStockMonitor.StrategyScanner | 8 策略定时/事件扫描、对子实时扫描、生命周期维护、业务 Outbox 发布 | 不阻塞行情接入 |
| MySQL | 最终事实、任务状态、审计、业务结果 | 不保存 Tick 明细 |
| Redis | 实时快照、短期流、V2 事件、Consumer Group | 不作为长期事实库 |

### 3.1 .NET 项目依赖

```mermaid
flowchart TB
    CONTRACTS["Contracts<br/>gRPC 与 BarLifecycleEventV2"]
    DOMAIN["Domain<br/>行情、对子、策略领域模型"]
    APP["Application<br/>接口、算法、协调逻辑"]
    INFRA["Infrastructure<br/>MySQL、Redis、OTel 实现"]
    STRATEGIES["Strategies<br/>8个纯规则策略"]
    API["Api"]
    WORKER["Worker"]
    SCANNER["StrategyScanner"]
    BACKTEST["Backtest / Self-test"]

    CONTRACTS --> APP
    DOMAIN --> APP
    APP --> INFRA
    APP --> STRATEGIES
    INFRA --> API
    INFRA --> WORKER
    INFRA --> SCANNER
    STRATEGIES --> SCANNER
    APP --> BACKTEST
```

## 4. 数据分层与存储边界

| 数据 | 本地 SQLite | Redis | MySQL | 保留规则 |
|---|---:|---:|---:|---|
| 未确认 Tick | 是 | 否 | 否 | ACK 后延迟清理，默认已确认记录保留 24 小时 |
| 当日 Tick 最新值 | 否 | 是 | 否 | 约 36 小时 TTL |
| 当日 Tick 短期流 | 否 | 是 | 否 | 正常 30 分钟，硬上限约 120 分钟 |
| 1m 盘中预览 | 否 | 是 | 否 | 约 72 小时 TTL，`officialConfirmed=false` |
| 未闭合官方 Bar | 可缓冲 | 是 | 否 | 当日活动状态 |
| 正式 5m/30m/60m/1d | 可缓冲 | 事件投影 | 是 | MySQL 最终事实，SDK 权威 |
| Bar/对子/策略 Outbox | 否 | 发布目标 | 是 | 发布状态和失败审计 |
| 股票池、质量、缺口、检查点 | 否 | 指标/锁 | 是 | 长期审计 |
| 对子、策略、回放、校准 | 否 | 实时通知 | 是 | 长期业务结果 |

```mermaid
flowchart LR
    VOLATILE["短期层<br/>内存 + Redis"] -->|"可重建"| REBUILD["从 SDK / MySQL 重建"]
    DURABLE["长期层<br/>MySQL"] -->|"唯一键 + revision"| FACT["最终一致事实"]
    SDK["东方掘金 SDK"] --> VOLATILE
    SDK --> DURABLE
    VOLATILE --> BUSINESS["盘中低延迟业务"]
    FACT --> BUSINESS
```

### 4.1 正式表与兼容表

正式 K 线物理表：

- `kline_bar_5m`：官方 5 分钟 K 线；
- `kline_bar_agg`：官方 30/60 分钟 K 线，名称保留但正式数据不再由 5 分钟聚合生成；
- `kline_bar_daily`：官方日线；
- `bar_event_outbox`、`bar_reconcile_log`、`bar_sync_checkpoint`：事件、修订和同步水位。

`quote_tick` 已删除。`quote_bar`、`kline_bar_1m`、旧 V1 实时 K 线类可能仍因迁移兼容或自测保留，但不属于 V2 生产主链，不得被新功能依赖。

## 5. 股票池构建与实时分片

目标股票池是沪深 A 股、非 ST、非北交所；明确排除 `SHSE.900xxx` 和 `SZSE.200xxx` B 股。历史状态不可用时必须记录来源和质量，不可把当前快照伪装成历史权威状态。

```mermaid
flowchart TB
    SDK["SDK 证券主数据与交易日"] --> FILTER["证券类型与代码双重过滤"]
    FILTER --> ASHARE{"是否沪深人民币 A 股?"}
    ASHARE -->|"否"| EXCLUDE["记录 exclusion_reason"]
    ASHARE -->|"是"| STATUS["上市、退市、ST、停牌状态"]
    STATUS --> QUALITY["status_source + status_quality"]
    QUALITY --> DAILY[("instrument_daily_status")]
    DAILY --> ELIGIBLE["最新交易日 is_eligible=true"]
    ELIGIBLE --> EXPORT["原子导出 .runtime/live-symbols.txt"]
    EXPORT --> COUNT["按目标100只/进程计算进程数"]
    COUNT --> SUP["Supervisor 稳定轮询分片"]
    SUP --> P1["worker-001"]
    SUP --> P2["worker-002"]
    SUP --> PN["worker-N"]
```

## 6. 实时 Tick 可靠接入

实时 Tick 采用“有界队列 + 每进程独立 SQLite WAL + gRPC 至少一次 + Redis 幂等事件”语义。SQLite 文件不共享，因此多进程之间不会争用同一个数据库锁。

```mermaid
sequenceDiagram
    participant SDK as 东方掘金SDK
    participant P as Python Live Worker
    participant Q as 有界内存队列
    participant O as 独立SQLite Outbox
    participant API as gRPC API
    participant R as Redis Tick Stream
    participant M as 内存最新行情

    SDK->>P: Tick 回调
    P->>Q: 非阻塞入队
    Q->>O: 最多200条或20ms批量事务
    O->>API: 未确认记录按序发送
    API->>M: eventId去重后更新L0
    API->>R: 更新当日latest并XADD固定分片
    R-->>API: 返回streamId
    API-->>O: ACK_STAGE_STREAM_APPENDED
    O->>O: 标记已确认

    Note over O,API: API/Redis中断时保留未确认记录并重发
    Note over API,R: Tick不写MySQL
```

Redis 主键：

```text
md:v2:tick:latest:{tradingDate}:{symbol}
md:v2:tick:stream:{tradingDate}:{00..15}
```

## 7. Tick 保留与 1 分钟盘中预览

1 分钟数据只服务盘中 VWAP、量能和快速策略，是可丢失、可重建的预览，不是正式 K 线。

```mermaid
flowchart LR
    TICK["当日 Tick Streams<br/>16分片"] --> GROUP["intraday-preview-v2"]
    GROUP --> CLAIM["XREADGROUP + XAUTOCLAIM"]
    CLAIM --> LUA["Redis Lua 原子更新"]
    LUA --> CUM["按股票维护累计量额水位"]
    LUA --> OHLC["1m OHLCV 预览"]
    OHLC --> BARS["md:v2:preview:1m:bars:{date}:{symbol}"]
    OHLC --> CHANNEL["md:v2:preview:1m:updated"]
    BARS --> FAST["快速策略与查询"]
    CLAIM --> ACK["处理成功后XACK"]

    RETENTION["TickStreamRetentionWorker"] --> TRIM["按时间和长度裁剪"]
    TRIM --> TICK
```

## 8. 官方四周期 K 线实时固化

实时采集进程同时订阅 `300s/1800s/3600s/1d`，每根官方 Bar 走与 Tick 相同的本地可靠 Outbox，但使用更强的 SQLite 提交级别。

```mermaid
flowchart LR
    SDK["官方 Bar 回调"] --> NORMALIZE["标准化 OfficialBarEnvelope"]
    NORMALIZE --> SQLITE["SQLite FULL 提交"]
    SQLITE --> GRPC["gRPC Official Bar Ingest"]
    GRPC --> CLOSED{"isClosed?"}
    CLOSED -->|"否"| ACTIVE["Redis活动Bar<br/>BarUpdated语义"]
    CLOSED -->|"是"| CANON["CanonicalBarWriter"]
    CANON --> MYSQL[("正式四周期K线")]
    CANON --> ACK["MySQL提交后ACK本地Outbox"]
```

正式 Bar 不从 Tick 聚合，也不从 5 分钟聚合 30/60 分钟。聚合逻辑只可用于离线校验，不能覆盖 SDK 官方事实。

## 9. Canonical Bar Writer 与修订生命周期

```mermaid
sequenceDiagram
    participant I as 实时或补数OfficialBar
    participant W as CanonicalBarWriter
    participant K as MySQL K线表
    participant C as bar_sync_checkpoint
    participant R as bar_reconcile_log
    participant O as bar_event_outbox

    I->>W: 规范化后的官方Bar
    W->>K: SELECT现有rowHash/revision
    alt 新槽位
        W->>K: INSERT revision=0
        W->>O: INSERT BarClosed V2
    else rowHash相同
        W->>C: 只推进水位
    else 内容变化
        W->>K: UPDATE并递增revision
        W->>R: 记录新旧值与来源
        W->>O: INSERT BarRevised V2
    end
    W->>C: 更新最后官方时间与健康状态
    W-->>I: 同一事务提交结果
```

```mermaid
stateDiagram-v2
    [*] --> Active: 官方未闭合Bar
    Active --> Closed: 首次正式提交
    Closed --> Closed: 相同rowHash幂等忽略
    Closed --> Revised: 官方内容变化
    Revised --> Revised: 再次校正并递增revision
```

## 10. Bar V2 可靠事件总线

`bar_event_outbox` 通过租约领取，发布到 16 个稳定分片。消费者至少一次接收，以 `eventId` 去重，以 `revision` 覆盖旧版本。

```mermaid
flowchart LR
    OUTBOX[("bar_event_outbox")] --> CLAIM["FOR UPDATE SKIP LOCKED<br/>领取租约"]
    CLAIM --> PUBLISH["Redis XADD<br/>md:v2:bar:event:00..15"]
    PUBLISH --> SAVE["保存stream_id并标记published"]
    PUBLISH -->|"临时失败"| RETRY["retry_waiting + next_attempt_at"]
    RETRY --> CLAIM
    PUBLISH -->|"超过上限"| FAILED["failed，保留人工检查"]

    SAVE --> STRATEGY["strategy-scanner-v2"]
    SAVE --> PAIR["pair-trend-realtime-v2"]
    SAVE --> SIGNALR["market-api-signalr-v2"]
```

独立 Consumer Group 是防止消息堆积相互传染的关键：对子服务慢不会抢走策略消息，浏览器断开也不会阻塞内部计算。

## 11. 盘中对子顶底实时扫描

对子包含 `.00` 与 `.11`～`.99`。上升趋势检查阶段高点，下降趋势检查阶段低点；四周期独立命中后按同股、同方向、时间窗口合并为一条实时事件。

```mermaid
flowchart TB
    EVENT["BarClosed / BarRevised"] --> GROUP["pair-trend-realtime-v2"]
    GROUP --> DEDUPE["eventId去重 + revision检查"]
    DEDUPE --> LOAD["读取该股该周期官方预热窗口"]
    LOAD --> TREND{"趋势方向"}
    TREND -->|"上升"| HIGH["检查High尾数"]
    TREND -->|"下降"| LOW["检查Low尾数"]
    TREND -->|"震荡/预热不足"| NONE["不生成候选"]
    HIGH --> PAIR{".00或.11至.99?"}
    LOW --> PAIR
    PAIR -->|"是"| HIT["候选命中<br/>TOP或BOTTOM"]
    PAIR -->|"否"| NONE
    HIT --> CONFIRM["后续同周期Bar确认或失效"]
    CONFIRM --> MERGE["同股票、同方向、多周期归并"]
    MERGE --> TX["live_event + live_hit + outbox同事务"]
    TX --> ACK["MySQL提交后XACK"]
    TX --> BIZEVENT["pair:v2:event"]
```

### 11.1 BarRevised 处理

```mermaid
flowchart LR
    REVISED["BarRevised"] --> RANGE["定位受影响计算窗口"]
    RANGE --> RELOAD["重新读取官方K线"]
    RELOAD --> RECOMPUTE["重算命中与事件"]
    RECOMPUTE --> KEEP{"原命中仍成立?"}
    KEEP -->|"是"| UPDATE["更新source_revision与评分"]
    KEEP -->|"否"| RETRACT["命中标记RETRACTED"]
    UPDATE --> EVENT["事件event_revision递增"]
    RETRACT --> EVENT
```

## 12. 8 个策略扫描

策略服务同时支持定时全市场扫描和 Bar 事件扫描。市场数据读取已经批量化：MySQL 按股票批次读取 30 分钟与日线，Redis Pipeline 并发读取最新行情和 1m 预览，避免逐股票 N+1 查询。

```mermaid
flowchart TB
    TIMER1["每60秒 Fast"] --> COORD["StrategyScanCoordinator"]
    TIMER2["每300秒 Observe"] --> COORD
    BAREVENT["30m / 1d Closed或Revised"] --> EVENTSCAN["BarEventStrategyWorker"]
    EVENTSCAN --> COORD
    COORD --> BATCH["股票分批"]
    BATCH --> REDIS["Redis Pipeline<br/>最新Tick + 1m预览"]
    BATCH --> MYSQL["MySQL窗口查询<br/>30m + 1d"]
    REDIS --> FEATURE["共享时点特征引擎"]
    MYSQL --> FEATURE
    FEATURE --> RULES["8个纯规则策略"]
    RULES --> SCORE{"达到资格分?"}
    SCORE -->|"否"| FUNNEL["记录过滤漏斗"]
    SCORE -->|"是"| SIGNAL["不可变策略信号"]
    SIGNAL --> MERGE["同股当日机会合并"]
    MERGE --> TX["信号 + 机会 + Outbox事务"]
    TX --> STREAM["strategy:v1:signal:event"]
```

扫描层：

| 层 | 周期 | 策略 |
|---|---:|---|
| Fast | 60 秒 | 分时 VWAP 量价共振、低开高走 VWAP 再启动 |
| Observe/Event | 300 秒或 30m/1d 事件 | 平台放量突破、均线回踩再启动、下跌浪二次探底反弹、强势趋势延续、逆势走强、强修复反弹 |

### 12.1 策略生命周期

```mermaid
stateDiagram-v2
    [*] --> New: 首次达到资格分
    New --> Strengthened: 重复命中且评分增强
    New --> Weakened: 6分钟未再次命中
    Strengthened --> Weakened: 6分钟未再次命中
    Weakened --> Active: 再次命中
    Weakened --> Disappeared: 18分钟未再次命中
    Active --> Disappeared: 超过失效窗口
    Active --> Revised: 来源Bar被修订
    Revised --> Active: 重算后仍成立
    Revised --> Disappeared: 重算后不成立
```

## 13. HTTP 查询、Swagger 与 SignalR

HTTP 提供一致快照和分页历史；SignalR 只提供实时增量。客户端重连后必须重新获取 HTTP 快照，再恢复订阅，不能依赖 SignalR 补历史消息。

```mermaid
sequenceDiagram
    participant C as 浏览器客户端
    participant API as REST API
    participant H as SignalR Hub
    participant R as Redis事件流
    participant M as MySQL事实库

    C->>API: 查询最新行情、正式K线、对子和策略快照
    API->>R: 读取实时Tick/1m预览
    API->>M: 读取正式K线与业务事实
    API-->>C: 返回快照、eventId、revision
    C->>H: 建立连接并订阅
    R->>H: Quote / Bar / Pair / Strategy事件
    H-->>C: 实时增量
    Note over C,H: 断线期间事件不做浏览器级历史重放
    C->>API: 重连后重新获取快照
    C->>H: 恢复订阅
```

### 13.1 API 功能入口

| 功能 | 路径 |
|---|---|
| 最新 Tick、近期 Tick、采集运行状态 | `/api/market/*` |
| 官方四周期 K 线 | `/api/market/bars*` |
| 历史批次与质量问题 | `/api/history/*` |
| 缺口检测、缺口分页、恢复批次与重试 | `/api/market-data/*` |
| 对子历史回测事件、命中、统计 | `/api/pair-trends/*` |
| 对子实时事件、命中、分片状态 | `/api/pair-trends/live/*` |
| 策略定义、信号、机会、扫描和回放校准 | `/api/strategies/*` |
| Swagger UI | `/swagger` |
| 行情与 Bar 推送 | `/hubs/market` |
| 策略推送 | `/hubs/strategy` |
| 存活/就绪 | `/health/live`、`/health/ready` |

## 14. 60 日历史 K 线增量与断点续传

分钟历史窗口为最近 60 个自然日且默认不含当天；日线不受此分钟窗口限制。下载范围内已有数据通过检查点和最新官方日期增量跳过。

```mermaid
flowchart TB
    START["历史回填或每日增量"] --> RANGE["解析目标日期范围"]
    RANGE --> LIMIT["分钟线裁剪到SDK授权窗口"]
    LIMIT --> UNIVERSE["加载严格A股股票池"]
    UNIVERSE --> COMPLETE["排除四周期均已完成的股票"]
    COMPLETE --> PARTITION["残余股票分摊到多进程"]
    PARTITION --> CP["读取股票+周期next_date"]
    CP --> CHUNK["分钟31天切片<br/>日线366天切片"]
    CHUNK --> SDK["SDK单股或小批量查询"]
    SDK --> FILTER["按交易日资格过滤"]
    FILTER --> UPSERT["唯一键幂等Upsert"]
    UPSERT --> WATERMARK["每个切片推进检查点"]
    WATERMARK --> MORE{"到目标日?"}
    MORE -->|"否"| CHUNK
    MORE -->|"是"| DONE["checkpoint=complete"]

    WATCHDOG["无进展看门狗"] --> DBPROGRESS{"数据库updated_at仍推进?"}
    DBPROGRESS -->|"是"| WAIT["继续等待SDK分区"]
    DBPROGRESS -->|"否"| STOP["终止挂起子进程<br/>保留next_date"]
```

## 15. 每日增量流水线

```mermaid
flowchart LR
    TIMER["每日16:20计划任务"] --> DATE["确定最近已完成交易日"]
    DATE --> PART["补齐未来月份分区"]
    PART --> POOL["构建当日严格股票池"]
    POOL --> DOWNLOAD["下载官方四周期增量"]
    DOWNLOAD --> QUALITY["执行质量检查"]
    QUALITY --> PAIR["重算有限窗口对子结果"]
    PAIR --> JAN{"当前是否1月?"}
    JAN -->|"否"| COMPLETE["daily_pipeline_run完成"]
    JAN -->|"是"| RETENTION["执行归档/清理预检"]
    RETENTION --> COMPLETE
```

## 16. 数据质量检查

```mermaid
flowchart LR
    BARS["5m / 30m / 60m / 1d"] --> COUNT["每交易日槽位数<br/>48 / 8 / 4 / 1"]
    BARS --> DUP["唯一键与重复"]
    BARS --> OHLC["OHLC关系与正价格"]
    BARS --> VA["成交量和成交额非负"]
    BARS --> SESSION["交易时段与5分钟对齐"]
    BARS --> SOURCE["官方来源、priority、confirmed"]
    COUNT --> REPORT[("bar_quality_run / issue")]
    DUP --> REPORT
    OHLC --> REPORT
    VA --> REPORT
    SESSION --> REPORT
    SOURCE --> REPORT
    REPORT --> GATE{"关键错误为0?"}
    GATE -->|"否"| GAP["创建缺口或修复任务"]
    GATE -->|"是"| READY["允许回放与业务计算"]
```

质量结果必须持久化，不以“进程退出码为 0”代替数据正确性。

## 17. 缺口检测与自动补数

缺口服务随 V2 数据架构同步变化：只检测和补充官方 `5m/30m/60m/1d`，不补 Tick，不补正式 1m。

```mermaid
flowchart TB
    STARTUP["Worker启动扫描"] --> DETECT["标准槽位检测"]
    BOUNDARY["每个已闭合5分钟边界"] --> DETECT
    ROLLING["盘中每15分钟滚动扫描"] --> DETECT
    CLOSE["15:10收盘终检"] --> DETECT
    DETECT --> COMPARE["股票池期望槽位 vs MySQL官方槽位"]
    COMPARE --> GAP[("market_data_gap")]
    GAP --> AGE{"分钟缺口是否在60日内?"}
    AGE -->|"否"| EXPIRED["source_expired<br/>停止无效重试"]
    AGE -->|"是"| RUN[("market_recovery_run / item")]
    RUN --> CLAIM["Python Recovery<br/>SKIP LOCKED + 租约"]
    CLAIM --> SDK["SDK官方历史查询"]
    SDK --> CANON["Canonical语义幂等写入"]
    CANON --> EVENT["BarClosed / BarRevised"]
    EVENT --> PAIR["对子重算"]
    EVENT --> STRATEGY["策略重算"]
    CANON --> VERIFY["重新核验缺口"]
    VERIFY --> STATUS["completed / retry_waiting / partial"]
```

## 18. 对子历史回放

```mermaid
flowchart TB
    RANGE["选择日期、股票和四周期"] --> LOAD["读取官方已确认K线"]
    LOAD --> ANALYZE["按时间顺序识别趋势和对子尾数"]
    ANALYZE --> CANDIDATE["产生TOP/BOTTOM候选"]
    CANDIDATE --> FUTURE["最多观察后续配置根数"]
    FUTURE --> STATUS["CANDIDATE / CONFIRMED / INVALIDATED"]
    STATUS --> MERGE["同股、同方向、时间窗、多周期归并"]
    MERGE --> EVENT[("pair_trend_event")]
    MERGE --> HIT[("pair_trend_hit")]
    EVENT --> API["分页、详情、统计接口"]
    HIT --> API
```

## 19. 8 策略逐时点历史回放与阈值校准

```mermaid
flowchart TB
    READY["数据就绪门"] --> RUN["创建strategy_replay_run"]
    RUN --> SYMBOL["股票级并发与断点"]
    SYMBOL --> CLOCK["按5分钟时间轴推进"]
    CLOCK --> SNAPSHOT["只使用观察时点之前的数据"]
    SNAPSHOT --> FAST["Fast策略逐5m评估"]
    SNAPSHOT --> SLOW["Observe策略逐30m评估"]
    FAST --> CROSS["记录阈值首次穿越"]
    SLOW --> CROSS
    CROSS --> SIGNAL[("strategy_replay_signal")]
    SIGNAL --> OUTCOME["D1/D3/D5/W1、MFE、MAE"]
    OUTCOME --> SPLIT["按时间训练/验证切分"]
    SPLIT --> CALIB["样本数、胜率、收益、稳定性校准"]
    CALIB --> RESULT[("strategy_calibration_result")]
    RESULT --> REVIEW["人工审核，不自动改线上阈值"]
```

## 20. 年度归档与清理

每年一月开始检查，分钟 K 线归档截止日为上一年 7 月 1 日之前。默认 dry-run；只有导出校验成功且显式启用 purge 才删除分区。

```mermaid
flowchart LR
    JAN["每年1月"] --> CUTOFF["计算截止日"]
    CUTOFF --> SELECT["选择5m和30/60m月份分区"]
    SELECT --> EXPORT["导出Zstandard Parquet"]
    EXPORT --> VERIFY["校验行数与SHA-256"]
    VERIFY -->|"失败"| KEEP["保留MySQL并记录失败"]
    VERIFY -->|"成功"| MANIFEST[("archive_manifest")]
    MANIFEST --> PURGE{"显式启用purge?"}
    PURGE -->|"否"| DRY["仅报告"]
    PURGE -->|"是"| DROP["删除已验证分区"]
    DROP --> AUDIT[("maintenance_job_run")]
```

日线、股票池、质量、缺口、对子、策略、回放和校准结果长期保留。

## 21. 健康检查与故障发现

`/health/live` 只证明 API 进程能执行代码；`/health/ready` 判断数据链路是否可服务。非交易时段不会因没有新 Tick 误报故障。

```mermaid
flowchart TB
    LIVE["/health/live"] --> PROCESS["进程存活"]
    READY["/health/ready"] --> REDIS["Redis Ping与连接"]
    READY --> MYSQL["MySQL连接与Schema"]
    READY --> OUTBOX["Bar Outbox失败数与最老积压"]
    READY --> PAIR["对子分片检查点"]
    READY --> SESSION{"当前是否交易时段?"}
    SESSION -->|"是"| COLLECTOR["采集心跳少于30秒"]
    SESSION -->|"是"| FRESH["最新Tick少于10秒"]
    SESSION -->|"否"| SKIP["跳过Tick新鲜度故障判定"]
    REDIS --> RESULT{"全部通过?"}
    MYSQL --> RESULT
    OUTBOX --> RESULT
    PAIR --> RESULT
    COLLECTOR --> RESULT
    FRESH --> RESULT
    SKIP --> RESULT
    RESULT -->|"是"| HEALTHY["Healthy"]
    RESULT -->|"否"| UNHEALTHY["Unhealthy + 具体失败原因"]
```

## 22. 可观测与运维页面

```mermaid
flowchart LR
    API["API日志、指标、Trace"] --> OTEL["OTel Collector"]
    WORKER["Worker日志、指标、Trace"] --> OTEL
    SCANNER["Scanner日志、指标、Trace"] --> OTEL
    OTEL --> PROM["Prometheus指标"]
    OTEL --> LOKI["Loki日志"]
    OTEL --> TEMPO["Tempo链路"]
    MYSQL["MySQL Exporter"] --> PROM
    REDIS["Redis Exporter"] --> PROM
    HTTP["Blackbox HTTP探测"] --> PROM
    PROM --> ALERT["告警规则"]
    PROM --> GRAFANA["Grafana中文运维总览"]
    LOKI --> GRAFANA
    TEMPO --> GRAFANA
    ALERT --> GRAFANA
    GRAFANA --> VIEW["服务状态、最后成功时间、Lag、错误与链路"]
```

推荐故障定位顺序：服务可用性 → 最后成功时间/水位 → Outbox 与 Stream Lag → Loki 错误日志 → Tempo 链路 → MySQL/Redis/主机资源。

## 23. 启停与每日运行时序

### 23.1 启动顺序

```mermaid
flowchart LR
    DOCKER["1. MySQL + Redis"] --> OBS["2. 可观测容器"]
    OBS --> API["3. AStockMonitor.Api"]
    API --> WORKER["4. AStockMonitor.Worker"]
    WORKER --> SCANNER["5. StrategyScanner"]
    SCANNER --> COLLECTOR["6. Python实时采集"]
    COLLECTOR --> RECOVERY["7. Python Recovery"]
```

停止顺序相反：先停止采集和补数，再等待 Outbox 排空，然后停止 Scanner、Worker、API，最后停止 Redis/MySQL。

### 23.2 一个交易日

```mermaid
timeline
    title A股监控程序单日运行
    08:50 : 检查MySQL、Redis、服务和采集任务
    09:15 : 刷新股票池与实时采集分片
    09:25 : 就绪检查开始要求采集心跳
    09:30-11:30 : Tick、官方Bar、预览、策略、对子、边界/滚动缺口扫描
    11:30-13:00 : 午休，保留服务与事件消费
    13:00-15:00 : 下午实时链路继续
    15:10 : 四周期收盘完整性终检
    16:20 : 每日官方K线增量、质量检查与离线计算
    盘后 : 缺口Recovery、回放、校准和维护任务
```

## 24. 故障隔离与自动恢复

| 故障 | 直接影响 | 恢复机制 |
|---|---|---|
| 单个 Python 采集进程退出 | 该分片短暂停顿 | Supervisor 重启；独立 SQLite Outbox 重发 |
| gRPC/API 中断 | 实时消息无法上传 | 本地 Outbox 保留，连接恢复后发送 |
| Redis 中断 | Tick 实时层与事件发布暂停 | 本地 Tick Outbox、MySQL Bar Outbox 保留 |
| MySQL 中断 | 正式 Bar、对子、策略事务暂停 | 提交前不 ACK；恢复后重放 |
| Bar Outbox Publisher 中断 | V2 Bar 事件积压 | 租约过期后重新领取 |
| 单个 Bar 分片消费者异常 | 对应分片延迟 | 每分片独立监督、Pending 接管 |
| 对子服务异常 | 对子延迟 | 不影响策略和 SignalR Consumer Group |
| 策略服务异常 | 策略延迟 | 不影响行情与对子；事件恢复后重放 |
| SDK 历史请求挂起 | 当前分区不返回 | 数据库进度看门狗、终止子进程、保留 next_date |
| 数据缺失/修订 | 局部结果不完整 | 缺口补数 → BarClosed/Revised → 业务重算 |
| 监控栈中断 | 暂时不可观察 | 不影响行情事实；Docker 卷恢复后继续采集 |

## 25. 当前生产主链与禁用路径

```mermaid
flowchart LR
    GOOD1["Tick → Redis短期层"] --> GOOD2["Tick → Redis 1m预览"]
    GOOD3["SDK官方四周期 → MySQL"] --> GOOD4["MySQL Outbox → V2 Bar Stream"]
    GOOD4 --> GOOD5["对子 / 策略 / SignalR"]

    BAD1["Tick → MySQL"] -.->|"禁止"| X1["非生产路径"]
    BAD2["Tick → 正式K线"] -.->|"禁止"| X1
    BAD3["5m聚合替代官方30/60m"] -.->|"禁止"| X1
    BAD4["正式1m落库与补数"] -.->|"禁止"| X1
    BAD5["V1 Bar Stream生产消费"] -.->|"禁止"| X1
```

## 26. 完整端到端功能链

```mermaid
flowchart LR
    A["东方掘金实时行情"] --> B["Windows多进程可靠采集"]
    B --> C["gRPC接入"]
    C --> D["Redis Tick与1m预览"]
    C --> E["Canonical官方K线写入"]
    E --> F["MySQL四周期事实"]
    F --> G["V2 BarClosed / BarRevised"]
    G --> H["对子实时扫描"]
    G --> I["8策略事件扫描"]
    D --> I
    H --> J["业务结果Outbox"]
    I --> J
    J --> K["SignalR实时通知"]
    F --> L["HTTP分页与历史快照"]
    K --> M["未来前端页面"]
    L --> M

    N["60日历史增量"] --> F
    O["四类缺口检测"] --> P["SDK自动补数"] --> E
    F --> Q["质量、对子回放、策略回放与校准"]
    R["Grafana可观测平台"] -.-> B
    R -.-> C
    R -.-> F
    R -.-> H
    R -.-> I
```

## 27. 后续开发边界

数据底座稳定后，前端只需要遵守两条调用原则：

1. 首屏、分页、断线恢复全部调用 HTTP 快照接口；
2. 连接 SignalR 后只处理增量，并以 `eventId` 去重、以 `revision/eventRevision` 覆盖旧状态。

前端不会直接访问 Redis、MySQL 或东方掘金 SDK，也不会自行重复实现对子和策略算法。

详细专题文档：

- [行情数据存储与官方 K 线补数 V2](./market-data-storage-and-kline-recovery-v2.md)
- [盘中行情数据底座](./intraday-market-data-core.md)
- [行情缺口恢复](./market-gap-recovery.md)
- [对子趋势顶底](./pair-trend-v2.md)
- [策略扫描服务](./strategy-scanner-service-plan.md)
- [V2 架构整改方案](./v2-architecture-remediation-plan.md)
