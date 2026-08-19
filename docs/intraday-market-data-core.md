# 盘中行情数据核心服务

本文说明已经落地的盘中行情可靠链路、缓存结构、落库规则和业务取数边界。

## 1. 当前链路

```text
东方掘金 SDK
  → Python 每进程 SQLite Outbox
  → gRPC 双向流
  → .NET L0 最新行情/短时 Tick 缓存
  → Redis Streams（16个固定分片）
  → .NET Persistence Worker
  → MySQL quote_tick
```

核心交付语义为“至少一次投递 + event_id 幂等”。Python只有在收到
`ACK_STAGE_STREAM_APPENDED` 后才把本地记录标记为已确认；MySQL Worker只有在事务提交后
才执行 `XACK`。

## 2. Python Outbox

每个采集进程使用独立文件：

```text
.runtime/outbox/{worker-id}.sqlite3
```

Outbox使用SQLite WAL和`FULL`同步级别。每条数据写入后获得持久化的
`session_id + worker_sequence`，断线或进程重启后按序重发。已确认数据默认保留24小时后清理，
方便故障期间进行人工核对。

相关环境变量：

| 变量 | 默认值 | 说明 |
|---|---:|---|
| `ASTOCK_OUTBOX_DIR` | `.runtime/outbox` | Outbox目录 |
| `ASTOCK_OUTBOX_BATCH_SIZE` | `500` | 每次租约记录数 |
| `ASTOCK_OUTBOX_RETRY_SECONDS` | `2` | 未确认事件重发间隔 |
| `ASTOCK_OUTBOX_ACK_RETENTION_SECONDS` | `86400` | 已确认记录保留时间 |

采集端已经取消直接向Redis发布，避免gRPC和Redis两个独立队列出现一边成功、一边丢失。

## 3. Redis结构

Tick按照稳定的FNV-1a股票代码哈希进入16个分片：

```text
dev:stream:market:raw:tick:00
...
dev:stream:market:raw:tick:15
```

同一股票始终进入同一个分片。写入时不使用固定`MAXLEN`，防止MySQL故障期间裁剪尚未落库的消息。
后续只有在持久化水位完成后才能执行安全的`XTRIM MINID`。

最新行情投影：

```text
md:v1:latest:{symbol}
```

该Hash保存`event_time_ms`、`worker_sequence`和序列化行情。只有更晚的数据才能覆盖，默认TTL为7天。
Redis投影是可重建缓存；Redis Streams和MySQL才承担可靠传递与最终固化。

## 4. 内存缓存与业务接口

每个API进程保存：

- 每只股票最新行情；
- 每只股票最近256条Tick环形缓存；
- 独立订阅通道，SignalR等消费者不再竞争同一个ChannelReader。

业务层统一依赖`IMarketDataReader`：

```csharp
GetLatestAsync(symbol)
GetRecentTicksAsync(symbol, since, limit)
```

查询顺序：

- 最新行情：L0内存 → Redis → MySQL；
- 最近Tick：L0环形缓存 → MySQL。

HTTP接口：

```text
GET /api/market/latest?symbol=SHSE.600000
GET /api/market/ticks/recent?symbol=SHSE.600000&seconds=300&limit=1000
GET /api/market/runtime
```

## 5. 持久化与故障处理

- Redis消费者组从分片起点创建，避免新消费者组跳过已有数据；
- 每个分片拥有独立监督循环，单个分片失败后会记录错误并自动重启；
- Worker启动及运行期间都会使用`XAUTOCLAIM`接管超时Pending；
- 合法Tick幂等写入`quote_tick`；
- 无效JSON或缺少payload的消息先写入`ingest_failure`，再执行`XACK`；
- `trading_date`按Asia/Shanghai交易日计算，时间戳按UTC固化；
- 数据库保存`session_id`、`worker_sequence`和`server_receive_time`，用于追踪顺序和链路延迟。

Redis建议开启AOF并使用`noeviction`。生产环境应把可靠Stream实例与普通缓存实例拆开，防止缓存内存压力影响可靠日志。

## 6. 运行

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-data-services.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\apply-database-migrations.ps1

$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\AStockMonitor.Api\AStockMonitor.Api.csproj

$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project .\src\AStockMonitor.Worker\AStockMonitor.Worker.csproj
```

开发环境已经启用`DurableIngestEnabled`和`PersistenceEnabled`。生产环境必须通过配置显式开启，
并通过安全配置注入Redis/MySQL密码。

## 7. 实时 K 线引擎

当前已经实现独立于 Tick 落库的 `bar-engine-v1` 消费者组：

```text
16个 Tick Stream 分片
  → 每分片单活动租约
  → RealtimeBarEngine
  → 1m / 5m / 30m / 60m / 1d 活动K线
  ├─ BarUpdated  → Redis最新投影 + Pub/Sub（允许合并）
  └─ BarClosed / BarRevised
       → 16个可靠Bar事件Stream
       → MySQL quote_bar + realtime_bar_event
```

每只股票仍固定进入同一个Tick分片。每个分片还使用30秒Redis租约，确保误启动多个
Windows Worker实例时，同一股票不会被两个有状态聚合器并行处理；实例故障后租约自动释放，
新实例通过`XAUTOCLAIM`接管Pending消息。

### 7.1 交易时段与切桶

- 时区固定为`Asia/Shanghai`/`China Standard Time`；
- 上午`09:30-11:30`，下午`13:00-15:00`；
- 1/5/30/60分钟分别在上午和下午起点切桶，绝不跨越午休；
- 11:30、15:00的收盘Tick归入前一根K线；
- 日线从09:30持续至15:00，但午休Tick会被过滤；
- 空周期不生成虚假K线。

### 7.2 OHLC、成交量和幂等

- 开盘价和收盘价按照`event_time`而不是到达顺序决定，支持乱序Tick；
- 最高价和最低价在内存中增量更新；
- 优先累加`last_volume/last_amount`；数据商未提供时使用累计值区间并将完整性标记为`false`；
- 正式采集使用`session_id + worker_sequence`去重；旧数据和模拟数据使用最多4096个近期
  `event_id`，去重信息只在股票级保存一次，不会在五个周期复制全日Tick ID；
- Tick的`receive_time`推进事件水位，当前处理时间记录生命周期事件。因此收盘后回放积压
  Tick时会先完整聚合，不会把每条旧Tick单独关闭。

### 7.3 生命周期事件

- `BarUpdated`：活动K线变化。新高/新低立即发布，其余变化默认最多每250毫秒发布一次；
- `BarClosed`：超过EOB 3秒后首次关闭；
- `BarRevised`：关闭后120秒内收到迟到Tick时递增`revision`；日线窗口默认到收盘后300秒；
- `RealtimeBarEngine.ReconcileOfficial`已经提供官方K线确认/修订入口。官方值覆盖时会提高
  `revision`、设置`official_confirmed=true`并继续发布同一种`BarRevised`，策略不需要区分数据通道。

核心当前直接用同一条Tick增量更新五个活动周期，计算量固定为`O(5)`，在业务语义上1分钟线
仍是标准最小K线。历史数据继续优先下载数据商原生5/30/60分钟及日线，本引擎不扫描历史Tick
重复生产全量历史K线。

### 7.4 Redis和MySQL结构

```text
md:v1:bar:state:{symbol}                 可恢复活动状态，TTL 2天
md:v1:bar:latest:{symbol}:{frequency}    最新活动/关闭K线，TTL 7天
md:v1:bar:updated                        BarUpdated Pub/Sub频道
dev:stream:market:bar:event:00..15       Closed/Revised可靠事件
md:v1:lock:bar-engine:00..15             单活动聚合器租约
```

MySQL：

- `quote_bar`保存每个`symbol + frequency + eob`的最新修订版本；
- `realtime_bar_event`按确定性`event_id`保存Closed/Revised审计记录；
- MySQL只固化关闭和修订K线，盘中每次Updated不会高频写库；
- Redis事件先发布、MySQL后提交、状态最后保存，最后才XACK Tick。任何中间失败都可重放，
  重复事件由确定性`event_id`和数据库唯一键消除。

数据库结构由`006_realtime_bar_engine.sql`升级。

### 7.5 查询接口

```text
GET /api/market/bars/latest?symbol=SHSE.600000&frequency=5m
GET /api/market/bars?symbol=SHSE.600000&frequency=5m&from=...&to=...&limit=1000
```

最新接口优先读取Redis活动投影，缺失时回退MySQL；范围接口返回MySQL已固化K线并按时间正序排列。
Swagger中已经包含接口、参数和标准K线模型。

## 8. 验证与后续接入

纯内存确定性自检：

```powershell
dotnet run --project .\src\AStockMonitor.Backtest\AStockMonitor.Backtest.csproj -- --bar-self-test
```

当前验收覆盖交易时段、午休、五周期、乱序OHLC、重复Tick、关闭、迟到修订和官方校正。
下一步是把东方掘金SDK的原生K线订阅接到`ReconcileOfficial`入口，并让对子顶底服务订阅统一
Bar事件；这两个业务适配器不再修改K线计算和存储语义。
