# 非交易日统一运行门禁

实施日期：2026-08-15  
判定时区：Asia/Shanghai  
数据库口径：`instrument_daily_status.trading_date` 存在 `is_eligible=TRUE` 股票。

## 执行规则

| 模块 | 交易日 | 非交易日 |
|---|---|---|
| V5 Tick池协调器 | 上一交易日成立池＋盘中新成立池 | 清空目标池与6个分配槽，状态为 `idle_non_trading_day` |
| Python Tick Supervisor | 按Redis目标池创建最多6×50订阅 | 保留Supervisor主进程，不创建SDK/Relay子进程 |
| 全市场快照 | 交易窗口每5秒调用 `current()` | 每30秒空闲检查，不调用SDK |
| 官方K线调度 | 按5m/30m/60m/1d边界创建补数 | 不创建补数任务 |
| 定时策略扫描 | 执行Fast/Observe扫描 | 跳过 |
| 策略生命周期 | 每分钟维护减弱/消失状态 | 跳过，不消耗周末时间 |
| 16:20日终流水线 | 增量K线、质量检查、对子计算 | SDK日历复核后直接跳过，不创建 `daily_pipeline_run` |
| API健康检查 | 校验行情新鲜度与采集心跳 | 仍检查MySQL/Redis，但不检查Tick新鲜度 |
| Prometheus行情告警 | 正常生效 | Collector、Snapshot、HotTick时段告警抑制 |

数据库门禁缓存30秒。Python快照与Tick Supervisor使用相同的 `instrument_daily_status` 口径；日终任务在开始数据库写入前再使用东方掘金交易日历确认。

## 2026-08-15周六验收

- 三个.NET服务：Running；
- API健康检查：Healthy；
- `astock_market_is_trading_day`：0；
- V5 Tick状态：`idle_non_trading_day`；
- Redis Tick目标池：0；SDK订阅子进程：0；
- Snapshot状态：`idle_outside_market_hours`；
- 新增策略生命周期运行：0；
- 新增当日官方K线补数运行：0；
- 手工执行计划任务同款日终入口：退出码0，新增 `daily_pipeline_run` 为0；
- 四项交易时段行情告警：0项触发；
- `AStockMonitor-Autostart`通过任务计划程序执行：返回码0。
