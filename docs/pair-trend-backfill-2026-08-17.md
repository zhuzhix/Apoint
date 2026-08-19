# 2026-08-17 对子顶底严格补算执行单

## 数据契约

- 股票池只来自 GM 对 `2026-08-17` 的精确交易日判断、上市/退市范围及
  `get_history_instruments` 当日 `sec_level/is_suspended`，来源标识固定为
  `dongcai-gm-history`。
- 不使用 2026-08-18 当前 ST/停牌状态，不复制前一交易日股票池，也不读取
  MySQL 原始 K 线作为替代数据。
- 只有 `sec_level=1` 且未停牌的证券可采集；其他级别全部排除。
- 通常每只 eligible 证券应收到 `5m=48、30m=8、60m=4、1d=1`，合计
  61 根。掘金官方在周期内无成交时不会生成 Bar；只有原始响应加两次独立
  单股复核（三次完整 OHLCV/哈希映射一致）才能逐 EOB 声明
  `verified no-trade`。API 必须验证已收 EOB 与声明缺口互斥且并集精确覆盖
  计划窗口。禁止使用 `fill_missing='Last'` 或任何合成 K 线。
- 未经三次一致证明的缺失、重复、计划外 EOB，以及同一 cycle 同 EOB 的
  不同哈希，仍会被严格拒绝并进入原有失败流程。
- `complete` 后 Python 继续等待 WebAPI 状态变为 `idle`，确认四周期水位均为
  `2026-08-17 15:00:00` 且 `lastError` 为空才返回退出码 0。

## 已完成的只读探针

正式任务 Python：

`C:\Users\Administrator\AppData\Local\Programs\Python\Python313\python.exe`

GM 3.0.186 对 2026-08-17 连续两轮历史股票池结果一致：

| 指标 | 数量 |
| --- | ---: |
| 上市/退市范围内沪深 A 股 | 5206 |
| 历史状态返回 | 5206 |
| missing / duplicate / date mismatch | 0 / 0 / 0 |
| `sec_level=1` | 5003 |
| `sec_level=2` | 150 |
| `sec_level=3` | 53 |
| 停牌 | 5 |
| eligible | 4998 |

对 `SHSE.600000` 的四周期只读抽查在日线午夜语义规范化后为
`48/8/4/1`，每个周期 missing=0、extra=0。

十项真实官方无成交缺口已经连续三轮确认：

- `SHSE.603089 / 5m / 11:30`
- `SHSE.603307 / 5m / 11:05`
- `SHSE.603389 / 5m / 11:10`
- `SHSE.603657 / 5m / 10:35、13:55、14:00`
- `SHSE.603721 / 5m / 13:50`
- `SHSE.688060 / 5m / 10:40`
- `SHSE.688357 / 5m / 10:30`
- `SHSE.688459 / 5m / 13:55`

因此本轮实际接收 K 线总数应为：

`4998 × 61 - 10 = 304868`

## 正式执行

在管理员 PowerShell 中执行。补算期间不启动常驻采集任务：

```powershell
$taskName = 'AStockMonitor-PairKlineCollector'
$pythonExe = 'C:\Users\Administrator\AppData\Local\Programs\Python\Python313\python.exe'
$collectorDir = 'C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\collector\pair_kline_collector'
$configPath = Join-Path $collectorDir 'config.local.json'

Stop-ScheduledTask -TaskName $taskName
& $pythonExe (Join-Path $collectorDir 'main.py') `
  --config $configPath `
  --backfill-date 2026-08-17 `
  --once
if ($LASTEXITCODE -ne 0) {
    throw "2026-08-17 严格补算失败，保持常驻任务停止并检查 collector.log/API 日志。"
}
Start-ScheduledTask -TaskName $taskName
```

## MySQL 验收

```sql
SELECT trading_date,status,is_trading_day,source,total_symbol_count,
       eligible_symbol_count,universe_version,payload_hash,synced_at
FROM authoritative_universe_sync
WHERE trading_date='2026-08-17';

SELECT COUNT(*) total,
       SUM(is_eligible=TRUE) eligible,
       SUM(is_st=TRUE) st_or_risk,
       SUM(is_suspended=TRUE) suspended,
       COUNT(DISTINCT universe_version) versions
FROM instrument_daily_status
WHERE trading_date='2026-08-17';

SELECT COUNT(*) hits,COUNT(DISTINCT symbol) symbols,
       SUM(frequency='5m') hits_5m,
       SUM(frequency='30m') hits_30m,
       SUM(frequency='60m') hits_60m,
       SUM(frequency='1d') hits_1d,
       MIN(eob) first_eob,MAX(eob) last_eob
FROM pair_trend_live_hit
WHERE trading_date='2026-08-17'
  AND algorithm_version='pair-trend-v3';

SELECT COUNT(*) events,COUNT(DISTINCT symbol) symbols,
       SUM(is_active=TRUE) active_events,
       MIN(first_seen_at) first_seen,MAX(last_seen_at) last_seen
FROM pair_trend_live_event
WHERE DATE(root_5m_eob)='2026-08-17'
  AND algorithm_version='pair-trend-v3';
```

`authoritative_universe_sync` 应为 `completed/true/dongcai-gm-history/5206/4998`；
状态表应为 `5206/4998` 且只有一个 universe version。结果数量由正式行情和算法
决定，不能预造固定值，但日期范围、算法版本及事件/命中外键必须一致。

本次改造不增加数据库表或迁移。

## 正式执行结果（2026-08-18）

- 正式 cycle：`12ef96ccca194e1e9209a9a17ca16aee`。
- 服务端完成时间：`2026-08-18 01:26:05`（Asia/Shanghai）。
- WebAPI 最终状态：`idle`，`lastError=null`；5m、30m、60m、1d 水位均为
  `2026-08-17 15:00:00`。
- 内存守恒：4998 只 eligible 证券、304868 根真实 K 线；十项无成交缺口均有
  三次一致证明，没有使用填充或合成数据。
- 正式库结果（算法版本均为 `pair-trend-v3`）：
  - `pair_trend_live_event`：33771 行，4791 只证券，8956 个活动事件；
  - `pair_trend_live_hit`：63988 行，其中 5m/30m/60m/1d 分别为
    51128/7551/4133/1176；
  - `pair_trend_lifecycle_event`：71446 行，其中 16191 行需要通知；
  - hit/event 与 lifecycle/event 孤儿记录均为 0，日期和算法版本异常记录均为 0。
- 客户端在服务端计算期间曾发生一次公网 HTTP 轮询超时；`Complete` 已在此前由
  WebAPI 接收，服务端计算没有中断。最终 `idle` 状态、水位和 MySQL 守恒查询共同
  确认本轮补算成功。
- 常驻任务 `AStockMonitor-PairKlineCollector` 已恢复；当前为 1 个监督进程、
  6 个 Worker，心跳版本 `2.2.0`，并已同步 2026-08-18 权威股票池。
