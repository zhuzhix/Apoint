# A股监控程序：系统架构图与功能流程图

> 本文档记录 V1 架构，已由 [V2 当前架构与流程图](./system-architecture-and-flows-v2.md) 取代。V2 已取消 MySQL Tick、Tick 生成正式 K 线以及 5m 聚合替代官方 30/60m 的设计；请勿再按本文档实施。

> 文档日期：2026-08-13  
> 当前技术栈：Windows + Docker Desktop/WSL2、.NET 10、Python 东方财富掘金 SDK、MySQL 8.4、Redis 8、Grafana/Prometheus/Loki/Tempo/OpenTelemetry。  
> 系统边界：只获取、处理、监控行情与研究信号，不连接交易账户，不调用委托或下单接口。

## 1. 系统总览

系统分为六个相互隔离的域：行情采集、实时数据底座、历史数据底座、缺口恢复、业务研究、查询与运维。MySQL 是最终事实库，Redis 是实时可靠传递与热点状态层，进程内存只保存短生命周期计算状态。

```mermaid
flowchart LR
    SOURCE["东方财富掘金终端<br/>行情与证券主数据"]

    subgraph COLLECT["行情采集域｜Windows"]
        SUPERVISOR["Python Supervisor"]
        TICKPROC["Tick 多进程采集<br/>每进程约100只股票"]
        OUTBOX["每进程独立 SQLite Outbox"]
        HISTORY["历史K线与补数多进程"]
    end

    subgraph CORE["实时数据底座｜.NET 10"]
        API["AStockMonitor.Api<br/>gRPC、REST、SignalR"]
        TICKSTREAM["Redis Tick Streams<br/>16个固定分片"]
        WORKER["AStockMonitor.Worker"]
        BARENGINE["实时K线引擎"]
        BAREVENT["Redis Bar Event Streams<br/>Closed / Revised"]
    end

    subgraph RESEARCH["研究与监控域｜.NET 10"]
        SCANNER["StrategyScanner<br/>8个策略"]
        PAIR["对子趋势顶底<br/>pair-trend-v3"]
        REPLAY["逐时点历史回放<br/>阈值校准"]
    end

    subgraph DATA["事实与状态"]
        REDIS[("Redis 8<br/>实时流、租约、热点投影")]
        MYSQL[("MySQL 8.4<br/>Tick、K线、质量、信号、审计")]
        ARCHIVE[("Parquet 年度归档")]
    end

    subgraph OPS["查询与运维"]
        WEB["浏览器<br/>Swagger、REST、SignalR"]
        OBS["Grafana + Prometheus<br/>Loki + Tempo + OTel"]
    end

    SOURCE --> SUPERVISOR --> TICKPROC --> OUTBOX --> API
    SOURCE --> HISTORY
    API --> TICKSTREAM --> WORKER --> BARENGINE --> BAREVENT
    TICKSTREAM --> REDIS
    BAREVENT --> REDIS
    WORKER --> MYSQL
    HISTORY --> MYSQL
    HISTORY --> BAREVENT
    BAREVENT --> SCANNER
    MYSQL --> SCANNER
    MYSQL --> PAIR
    MYSQL --> REPLAY
    SCANNER --> MYSQL
    PAIR --> MYSQL
    REPLAY --> MYSQL
    MYSQL --> ARCHIVE
    MYSQL --> API --> WEB
    REDIS --> API
    API --> OBS
    WORKER --> OBS
    SCANNER --> OBS
```

## 2. 部署架构

东方财富 SDK 必须运行在 Windows。MySQL、Redis 和监控栈当前运行在同一台 Windows 主机的 Docker Desktop/WSL2 中；.NET 服务可留在 Windows，也可迁移到 Linux。服务分机后，只需保证内网端口可达，不改变数据契约。

```mermaid
flowchart TB
    subgraph WIN["Windows 行情主机｜必须保留东方财富终端"]
        GM["东方财富掘金终端与 Python SDK"]
        PY["Python Supervisor、Tick Worker、History Worker、Recovery Worker"]
        API[".NET 10 API<br/>5222 HTTP、5001 gRPC"]
        WORKER[".NET 10 Worker"]
        SCANNER[".NET 10 StrategyScanner"]
        WINEXP["windows_exporter｜9182"]
        GM --> PY --> API
    end

    subgraph DOCKER["Docker Desktop / WSL2 数据与监控层"]
        MYSQL[("MySQL｜3306")]
        REDIS[("Redis｜6379")]
        OTEL["OpenTelemetry Collector｜4317/4318"]
        PROM["Prometheus｜9090"]
        LOKI["Loki｜3100"]
        TEMPO["Tempo｜3200"]
        GRAFANA["Grafana｜3000"]
        EXPORTERS["MySQL、Redis、Blackbox Exporter"]
        OTEL --> LOKI
        OTEL --> TEMPO
        PROM --> GRAFANA
        LOKI --> GRAFANA
        TEMPO --> GRAFANA
        EXPORTERS --> PROM
    end

    PY --> MYSQL
    PY --> REDIS
    API --> MYSQL
    API --> REDIS
    WORKER --> MYSQL
    WORKER --> REDIS
    SCANNER --> MYSQL
    SCANNER --> REDIS
    API --> OTEL
    WORKER --> OTEL
    SCANNER --> OTEL
    WINEXP --> PROM

    BROWSER["本机浏览器"] --> API
    BROWSER --> GRAFANA
```

### 可扩展部署

```mermaid
flowchart LR
    WIN["Windows 行情节点<br/>SDK + Python采集"] -->|"gRPC / Redis / MySQL 内网"| DATA["Linux 数据节点<br/>MySQL + Redis"]
    DATA --> APP["Linux 应用节点<br/>API + Worker + StrategyScanner"]
    WIN -->|"OTLP + 主机指标"| MON["Linux 监控节点<br/>Grafana监控栈"]
    DATA --> MON
    APP --> MON
```

## 3. 实时 Tick 可靠采集流程

核心语义是“至少投递一次 + `event_id` 幂等”。SQLite Outbox 不共享文件，因此多进程不会互相锁库；每个进程只写自己的 WAL 文件。

```mermaid
sequenceDiagram
    participant SDK as 东方财富SDK
    participant P as Python采集进程
    participant O as 独立SQLite Outbox
    participant A as .NET gRPC API
    participant R as Redis Tick Stream
    participant W as Tick持久化Worker
    participant M as MySQL quote_tick

    SDK->>P: Tick回调
    P->>O: 先持久化 session_id + sequence
    P->>A: gRPC双向流发送
    A->>R: XADD到股票固定分片
    R-->>A: Stream写入成功
    A-->>P: ACK_STAGE_STREAM_APPENDED
    P->>O: 标记已确认
    W->>R: XREADGROUP / XAUTOCLAIM
    W->>M: event_id幂等批量写入
    M-->>W: MySQL事务提交
    W->>R: XACK

    Note over P,O: 断线或重启后按序重发未确认记录
    Note over W,M: 无效消息先写 ingest_failure，再确认消息
```

### Tick 数据的职责划分

| 数据 | 位置 | 作用 |
|---|---|---|
| 采集未确认 Tick | 每进程 SQLite Outbox | 抵御采集进程、gRPC、Redis短时故障 |
| 待落库 Tick | Redis `dev:stream:market:raw:tick:00..15` | 可靠传递、消费者组重放 |
| 最新行情 | 内存 + Redis `md:v1:latest:{symbol}` | 低延迟业务读取 |
| 固化 Tick | MySQL `quote_tick` | 最终审计、补算和最近Tick查询 |

## 4. 实时 K 线引擎流程

实时 K 线由 Tick 增量生成 `1m/5m/30m/60m/1d`。盘中更新走低延迟通道，关闭和修订走可靠通道；MySQL 不承受每个 `BarUpdated` 的高频写入。

```mermaid
flowchart LR
    TICK["Redis Tick Streams<br/>16分片"] --> LEASE["分片租约 + 消费组<br/>单股票单活"]
    LEASE --> ENGINE["RealtimeBarEngine<br/>事件时间、午休切段、乱序去重"]

    ENGINE --> M1["1分钟活动K线"]
    ENGINE --> M5["5分钟活动K线"]
    ENGINE --> M30["30分钟活动K线"]
    ENGINE --> M60["60分钟活动K线"]
    ENGINE --> D1["日线活动K线"]

    M1 --> LIFE["生命周期判定"]
    M5 --> LIFE
    M30 --> LIFE
    M60 --> LIFE
    D1 --> LIFE

    LIFE --> UPDATED["BarUpdated<br/>Redis投影 + Pub/Sub"]
    LIFE --> CLOSED["BarClosed<br/>可靠Stream"]
    LIFE --> REVISED["BarRevised<br/>可靠Stream"]

    UPDATED --> LIVE["盘中页面和快速策略"]
    CLOSED --> DB["MySQL quote_bar<br/>realtime_bar_event"]
    REVISED --> DB
    CLOSED --> STRATEGY["策略与对子等统一消费者"]
    REVISED --> STRATEGY

    OFFICIAL["东方财富官方K线<br/>历史回填或缺口恢复"] --> RECONCILE["ReconcileOfficial"]
    RECONCILE -->|"新槽位"| CLOSED
    RECONCILE -->|"内容变化"| REVISED
    RECONCILE -->|"完全相同"| NOOP["幂等忽略"]
```

### K 线生命周期

```mermaid
stateDiagram-v2
    [*] --> Active: 第一笔合法Tick
    Active --> Active: Tick更新OHLC与量额
    Active --> Closed: EOB后超过关闭宽限
    Closed --> Revised: 迟到Tick进入修订窗口
    Closed --> Revised: 官方K线覆盖或校准
    Revised --> Revised: 新修订版本
    Closed --> Final: 修订窗口结束
    Revised --> Final: 修订窗口结束
    Final --> Revised: 官方恢复数据明确校正
```

## 5. 行情读取与对外服务流程

业务层只依赖统一读接口，不关心数据来自内存、Redis 还是 MySQL。

```mermaid
flowchart TB
    CLIENT["浏览器或内部业务"] --> API["AStockMonitor.Api"]

    API --> LATEST{"最新行情或最新K线?"}
    LATEST -->|"命中"| L0["进程内存 L0"]
    L0 -->|"未命中"| CACHE["Redis热点投影"]
    CACHE -->|"未命中"| FACT["MySQL事实表"]

    LATEST -->|"历史范围、分页、审计"| FACT

    API --> REST["REST + Swagger"]
    API --> MARKET_HUB["SignalR /hubs/market"]
    API --> STRATEGY_HUB["SignalR /hubs/strategy"]
    API --> HEALTH["/health/live、/health/ready"]
```

主要功能入口：

- 行情：`/api/market/*`、`/api/market/bars*`；
- 历史底座：`/api/history/*`；
- 缺口恢复：`/api/market-data/gaps*`、`/api/market-data/recovery-runs*`；
- 对子趋势：`/api/pair-trends/*`；
- 8策略与回放：`/api/strategies/*`；
- 接口说明：`/swagger`。

## 6. 历史 K 线数据底座流程

历史批处理不经过实时 Tick gRPC 链路，直接批量、幂等写 MySQL。日线不受分钟窗口限制；分钟线自动裁剪到最近 60 个自然日且不含当天，绝不伪造范围外数据。

```mermaid
flowchart TB
    START["历史回放或每日增量任务"] --> PARTITION["补齐MySQL未来月份分区"]
    PARTITION --> UNIVERSE["构建按交易日变化股票池<br/>沪深、非ST、上市状态"]
    UNIVERSE --> DOWNLOAD["6个并发独立分区进程<br/>每分区最多100只、20只联合请求、31天切片"]
    DOWNLOAD --> PARTSTATE[("bar_ingest_partition<br/>独立partition_id / PID / 心跳 / 进度")]
    PARTSTATE --> WATCHDOG{"分区心跳或进度心跳超时?"}
    WATCHDOG -->|"是"| ISOLATE["只终止该partition_id<br/>其他分区继续执行"]
    WATCHDOG -->|"否"| KEEP["维持该分区运行"]
    DOWNLOAD --> CHECKPOINT["股票 + 频率断点<br/>bar_ingest_checkpoint"]
    DOWNLOAD --> M5[("kline_bar_5m")]
    DOWNLOAD --> D1[("kline_bar_daily")]

    M5 --> AGG["6进程交易时段聚合"]
    AGG --> M30[("kline_bar_agg 30m")]
    AGG --> M60[("kline_bar_agg 60m")]

    M5 --> QUALITY["质量检查 quality-v1"]
    D1 --> QUALITY
    M30 --> QUALITY
    M60 --> QUALITY
    QUALITY --> ISSUES[("bar_quality_run<br/>bar_quality_issue")]

    QUALITY --> GATE{"质量门通过?"}
    GATE -->|"否"| REPAIR["生成缺口、修复后复检"]
    REPAIR --> QUALITY
    GATE -->|"是"| PAIR["对子趋势顶底回测"]
    GATE -->|"是"| REPLAY["8策略逐时点历史回放"]

    PAIR --> PAIRDB[("pair_trend_event / hit")]
    REPLAY --> CALIB[("strategy_replay_*<br/>strategy_calibration_result")]
```

### 历史数据质量门

```mermaid
flowchart LR
    INPUT["5m、30m、60m、日线"] --> MISS["缺失数量<br/>48 / 8 / 4 / 1"]
    INPUT --> DUP["主键与重复检查"]
    INPUT --> OHLC["OHLC合法性"]
    INPUT --> VA["成交量、成交额非负"]
    INPUT --> SESSION["交易时段与5分钟对齐"]
    INPUT --> COMPONENT["聚合组成数量"]
    INPUT --> SAMPLE["抽样对比SDK官方30/60分钟"]
    MISS --> REPORT["质量运行与问题明细"]
    DUP --> REPORT
    OHLC --> REPORT
    VA --> REPORT
    SESSION --> REPORT
    COMPONENT --> REPORT
    SAMPLE --> REPORT
```

## 7. 行情缺口检测与自动恢复流程

缺口恢复只补官方 K 线，不伪造历史 Tick。恢复后的官方 K 线重新进入统一 Bar 事件链，因此策略消费者不需要维护第二套数据通道。

```mermaid
flowchart LR
    POOL["交易日股票池"] --> SLOT["标准K线槽位生成器"]
    STORED["历史K线 + quote_bar"] --> SLOT
    SLOT --> DETECT["缺失槽位检测<br/>上午、下午分别合并"]
    DETECT --> GAP[("market_data_gap")]
    GAP --> RUN[("market_recovery_run / item")]
    RUN --> CLAIM["Python多进程领取<br/>FOR UPDATE SKIP LOCKED + 租约"]
    CLAIM --> SDK["东方财富官方K线"]
    SDK --> HISTORY["历史表幂等写入"]
    HISTORY --> OFFICIAL["Redis Official Bar Streams<br/>16分片"]
    OFFICIAL --> RECONCILE["实时K线引擎官方校准"]
    RECONCILE --> BARDB["quote_bar + BarClosed / BarRevised"]
    BARDB --> VERIFY["从MySQL重新核算缺口"]
    VERIFY -->|"仍缺失"| CLAIM
    VERIFY -->|"归零"| RECALC["受影响股票与时间范围重算"]
    RECALC --> DONE["completed / partial"]
```

## 8. 8 个策略实时扫描流程

策略服务独立部署。策略异常不会阻塞 Tick ACK、K 线生成或行情落库。

```mermaid
flowchart TB
    UPDATED["BarUpdated<br/>低延迟提示"] --> FAST["快速层｜每60秒<br/>2个分时策略"]
    CLOSED["BarClosed / BarRevised<br/>可靠Stream"] --> EVENT["事件层｜相关股票立即扫描"]
    TIMER["交易时段定时器"] --> OBSERVE["观察层｜每300秒<br/>其余6个策略"]
    MYSQLK["MySQL已完成K线"] --> FEATURE["共享时点特征引擎"]
    REDISQ["Redis最新行情和活动K线"] --> FEATURE
    FAST --> FEATURE
    EVENT --> FEATURE
    OBSERVE --> FEATURE

    FEATURE --> RULES["8个纯规则策略<br/>价格、量能、VWAP、均线、平台、形态"]
    RULES --> RESULT{"是否达到资格分?"}
    RESULT -->|"否"| FUNNEL["过滤漏斗与原因统计"]
    RESULT -->|"是"| SIGNAL["不可变策略信号事件"]
    SIGNAL --> MERGE["同股票当日机会合并<br/>增强、减弱、消失、修订"]
    MERGE --> TX["MySQL事务<br/>信号 + 机会 + Outbox"]
    TX --> OUTBOX["Strategy Outbox Publisher"]
    OUTBOX --> STREAM["Redis策略事件Stream"]
    STREAM --> API["Web API + SignalR"]
```

### 策略分层

| 扫描层 | 策略 | 触发方式 |
|---|---|---|
| 快速层 | 分时 VWAP 量价共振、低开高走 VWAP 再启动 | 每60秒 + 分钟K事件 |
| 观察/事件层 | 平台放量突破、均线回踩再启动、下跌浪二次探底反弹、强势趋势延续、逆势走强、强修复反弹 | 每300秒 + 30分钟/日线关闭或修订 |

## 9. 8 策略逐时点历史回放与阈值校准

回放严格按观察时点构建快照：不能读取观察点之后的 5 分钟、30 分钟或日线。它记录每个阈值的首次穿越，而不是用收盘后最高分倒推信号。

```mermaid
flowchart TB
    READY["数据就绪门禁"] --> R1{"无运行中的回填批次?"}
    R1 --> R2{"30分钟聚合覆盖?"}
    R2 --> R3{"质量运行覆盖且无关键错误?"}
    R3 -->|"否"| STOP["拒绝正式回放"]
    R3 -->|"是"| SYMBOLS["4并发股票级回放与断点"]

    SYMBOLS --> CLOCK["按5分钟时间轴推进"]
    CLOCK --> SNAPSHOT["构造时点快照<br/>当日累计价量VWAP、历史日线、已闭合30m"]
    SNAPSHOT --> FAST["Fast策略：每根5m闭合点评估"]
    SNAPSHOT --> SLOW["Observe/Event策略：每根30m闭合点评估"]
    FAST --> CROSS["记录75/80/85/90/95首次穿越"]
    SLOW --> CROSS
    CROSS --> EVIDENCE[("strategy_replay_signal<br/>特征与参数快照")]
    EVIDENCE --> OUTCOME["计算D1/D3/D5/W1<br/>MFE5/MAE5"]
    OUTCOME --> SPLIT["按时间70%训练 / 30%验证"]
    SPLIT --> CALIBRATE["样本数、均值、胜率、稳健性校准"]
    CALIBRATE --> RESULT[("strategy_calibration_result")]
    RESULT --> REPORT["Markdown校准报告 + 分页API"]
    REPORT --> MANUAL["人工审核<br/>不自动修改线上阈值"]
```

## 10. 对子趋势顶底流程

对子尾数包含 `.00`、`.11`～`.99`。上升趋势检查 K 线高点形成顶部候选；下降趋势检查低点形成底部候选。

```mermaid
flowchart LR
    BARS["已闭合5m / 30m / 60m / 日线"] --> PAST["只读取候选K线之前的数据"]
    PAST --> TREND{"EMA20 / EMA60趋势"}
    TREND -->|"上升"| HIGH["High尾数命中对子"]
    TREND -->|"下降"| LOW["Low尾数命中对子"]
    TREND -->|"震荡或预热不足"| IGNORE["不生成候选"]
    HIGH --> TOP["TOP候选"]
    LOW --> BOTTOM["BOTTOM候选"]
    TOP --> WATCH["最多观察后续3根同周期K线"]
    BOTTOM --> WATCH
    WATCH --> STATUS{"确认条件或创新高/新低?"}
    STATUS --> CONFIRMED["CONFIRMED"]
    STATUS --> INVALID["INVALIDATED"]
    STATUS --> CANDIDATE["CANDIDATE"]
    CONFIRMED --> MERGE["同股票、同顶底、10日窗口多周期归并"]
    INVALID --> MERGE
    CANDIDATE --> MERGE
    MERGE --> DB[("pair_trend_event<br/>pair_trend_hit")]
```

## 11. 数据存储总图

```mermaid
flowchart TB
    subgraph REDIS["Redis｜实时、可恢复、可重建"]
        RTICK["raw tick streams"]
        RBAR["bar event streams"]
        ROFFICIAL["official bar streams"]
        RSTRATEGY["strategy event stream"]
        PROJECTION["latest quote / latest bar投影"]
        LOCKS["分片租约、扫描租约、消费组水位"]
    end

    subgraph MYSQL["MySQL｜最终事实与审计"]
        MARKET["quote_tick、quote_bar、realtime_bar_event"]
        HISTORY["instrument_daily_status、kline_bar_1m/5m/agg/daily"]
        QUALITY["bar_ingest_*、bar_quality_*"]
        RECOVERY["market_data_gap、market_recovery_*"]
        PAIR["pair_trend_*、pair_pivot_signal"]
        STRATEGY["strategy_definition/version/scan/signal/opportunity/outbox"]
        CALIB["strategy_replay_*、strategy_calibration_result"]
        MAINTAIN["archive_manifest、maintenance_job_run"]
    end

    subgraph LOCAL["Windows本地"]
        OUTBOX["每采集进程 SQLite Outbox"]
        TOKEN["忽略提交的 config.local.json<br/>东方财富Token"]
    end

    subgraph FILES["文件归档"]
        PARQUET["Zstandard Parquet"]
        REPORTS["回放与校准 Markdown 报告"]
    end
```

## 12. 运维监控与故障定位流程

```mermaid
flowchart LR
    API["API"] -->|"OTLP指标、日志、链路"| OTEL["OpenTelemetry Collector"]
    WORKER["Worker"] -->|"OTLP"| OTEL
    SCANNER["StrategyScanner"] -->|"OTLP"| OTEL
    OTEL --> LOKI["Loki日志"]
    OTEL --> TEMPO["Tempo链路"]
    OTEL --> PROM["Prometheus指标"]

    WIN["Windows主机指标"] --> PROM
    MYSQL["MySQL Exporter"] --> PROM
    REDIS["Redis Exporter"] --> PROM
    HTTP["Blackbox HTTP探测"] --> PROM

    PROM --> ALERT["告警规则"]
    PROM --> GRAFANA["Grafana中文运维总览"]
    LOKI --> GRAFANA
    TEMPO --> GRAFANA
    ALERT --> GRAFANA

    GRAFANA --> TRIAGE["故障定位顺序"]
    TRIAGE --> STEP1["1. 服务可用性与当前告警"]
    STEP1 --> STEP2["2. 各环节最后成功时间与水位"]
    STEP2 --> STEP3["3. Loki错误日志"]
    STEP3 --> STEP4["4. Tempo请求链路"]
    STEP4 --> STEP5["5. Prometheus目标与资源"]
```

## 13. 年度归档与数据清理

```mermaid
flowchart LR
    JAN["每年1月维护任务"] --> CUTOFF["计算截止日<br/>上一年7月1日前"]
    CUTOFF --> SELECT["选择5m与30/60m月分区"]
    SELECT --> EXPORT["流式导出Zstandard Parquet"]
    EXPORT --> VERIFY["校验行数 + SHA-256"]
    VERIFY -->|"失败"| KEEP["保留MySQL分区并记录失败"]
    VERIFY -->|"成功"| MANIFEST["写archive_manifest"]
    MANIFEST --> PURGE{"显式启用purge?"}
    PURGE -->|"否"| DRYRUN["仅报告，不删除"]
    PURGE -->|"是"| DROP["DROP已验证分区"]
    DROP --> AUDIT["maintenance_job_run审计"]
```

日线、股票池历史、质量结果、对子结果、策略证据和校准结果长期保留；只对分钟 K 线及其 30/60 分钟聚合执行年度归档清理。

## 14. 故障隔离与恢复原则

| 故障 | 影响范围 | 自动恢复依据 |
|---|---|---|
| 单个 Python 采集进程退出 | 该进程股票分片短暂停顿 | 独立 SQLite Outbox、Supervisor重启、未确认重发 |
| API/gRPC 短时不可用 | Tick积压在本地Outbox | 断线重连后按序重发 |
| Redis 短时不可用 | 实时链路暂停 | Outbox保留；Stream消费者恢复后继续 |
| MySQL 短时不可用 | Redis Stream Pending增长 | MySQL提交前不XACK，恢复后重放 |
| K线Worker退出 | Bar生成暂停 | 消费组Pending、XAUTOCLAIM、Redis活动状态恢复 |
| StrategyScanner退出 | 策略提示暂停，不影响行情 | Bar事件Pending、MySQL已完成K线、扫描断点 |
| 历史下载退出 | 当前批次中断 | 股票+频率检查点、幂等写入 |
| 数据缺失或内容错误 | 局部周期不完整 | 缺口任务、官方K线回放、BarRevised、派生重算 |
| 监控栈退出 | 不影响业务数据链路 | Docker持久卷，恢复后重新采集 |

## 15. 完整端到端功能链

```mermaid
flowchart LR
    A["实时Tick"] --> B["可靠接入"] --> C["Tick固化"] --> D["实时K线"] --> E["关闭/修订事件"] --> F["策略与对子"] --> G["API/SignalR"] --> H["浏览器"]
    I["历史K线"] --> J["断点下载"] --> K["聚合与质量"] --> L["历史回放与校准"] --> F
    M["缺口检测"] --> N["官方补数"] --> E
    A --> O["OpenTelemetry"]
    C --> O
    D --> O
    F --> O
    O --> P["Grafana故障发现"]
```

这条链路中的实时提示允许合并，但可靠事实不可跳过：采集先本地固化，Redis Stream 消息只在 MySQL 事务提交后确认，历史和恢复任务均使用断点、租约和唯一键收敛到同一最终状态。
