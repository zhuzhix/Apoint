# 四周期对子 K 线采集器

该程序只负责从东方财富掘金 SDK 取得已闭合 K 线并发送给 WebAPI。它不连接 Redis、MySQL，也不执行对子计算。

## 固定调度规则

- 四周期采集常驻一个监督进程和 **6 个采集 Worker 进程**；另有 **1 个波段日 K 采集进程**。操作系统中总共会看到 8 个本项目 Python 进程。
- 每个 Worker 每次最多负责 200 只股票；超过 1200 只的部分留在内存队列，Worker 空闲后继续领取。
- SDK 内部请求默认每批 20 只，避免把“一个进程负责 200 只”误解为一次向 SDK 请求 200 只。
- 单只股票失败会重新进入队列。连续第三次失败后，本地持久化并上报 WebAPI，黑名单有效期固定 24 小时。
- 整个 SDK 分组在某一周期/窗口没有任何有效计划行时，按供应商频率暂不可用处理：显式中止本轮并稍后重试，不增加任何股票的失败次数，也不允许把整组空响应伪装成无成交。
- GM `1026` 更新令牌错误及明确的 Token 无效/过期错误属于供应商鉴权故障：整轮熔断并保持降级心跳，绝不复制为个股失败或写入黑名单。启动及每 5 分钟会用最近已收盘交易日的一小段官方 `history` 做只读授权预检。
- 一旦产生黑名单，本轮 cycle 会显式 `abort`，不会用部分股票调用 `complete`。下一轮由 WebAPI 排除有效黑名单后重新下发完整计划。
- 成功时必须与 WebAPI 下发的股票池完全一致，才调用 `complete` 推进水位。
- WebAPI 连接被对端关闭、连接重置或写入管道中断时，幂等的计划/股票池/批次/心跳请求按 1、2、4 秒有限重试；仍失败才显式中止当前轮次。`--once`/历史补采仍以非零状态退出。HTTP 业务错误、JSON 协议错误和 Worker 进程池致命错误不会被当作可恢复传输断连。
- 增量窗口的 `from` 是排他下界。gm 如果重复返回同日、同证券、同周期且 `eob == from` 的合法已闭合旧 K 线，采集端会在完整性校验前丢弃，不推送、不计数、不参与稀疏证明。任何 `eob < from`、越过 `to`、窗口内非法 EOB、跨日、错证券或错周期仍会严格失败。

## 波段日 K 采集进程

当正式 `pair-trend-v3` 的底部事件首次达到 `FOCUS` 后，WebAPI 创建持久化任务。`wave_history_worker.py` 以拉取方式领取任务，避免云端 WebAPI 反向连接本地采集机：

- 单进程常驻，每次最多领取 200 只股票，其余任务继续保留在 WebAPI 队列；
- 按任务截止日取得沪深一致的交易日历，只获取最近 120 个已完成交易日的未复权日 K；
- Python 只校验并上传官方 OHLCV 和来源哈希，不计算 RSI、MACD、均线、形态或最终评分；
- WebAPI 在内存中完成评分并写回对应对子事件，随后释放该股票的日 K 会话；
- 同一股票存在多个重点底部事件时，日 K 可复用，但每个事件仍按自己的截止日独立评分；
- 供应商整体不可用不会消耗个股三次重试次数；普通数据错误最多重试三次后明确失败。

## 当日权威股票池

监督进程在领取任何采集计划前，先按 Asia/Shanghai 动态取得当天日期，并分别调用 gm 的 SHSE、SZSE 交易日接口。两个市场的判断必须一致：

- 非交易日向 WebAPI 明确同步空股票池；
- 交易日调用 `get_instruments` 获取当前沪深股票全集，严格过滤为 WebAPI 认可的沪深 A 股代码，并过滤尚未上市或当日前已经退市的历史证券；
- 每次同步连续读取两轮当前快照，规范化哈希完全一致才提交，避免跨时点拼成半快照；
- 正式 gm SDK 3.0.186 的 `skip_st=True` 对部分当前 ST 股票不生效，因此不把该参数当正确性门禁。ST 严格按当前官方证券名称的 `ST/*ST/S*ST/SST` 前缀识别，停牌使用 `is_suspended` 字段；ST 和停牌股票仍完整上报，由 WebAPI 标记为不可采集；
- 当日有效沪深 A 股少于 4000 只，或排除 ST、停牌后可采集股票少于 4500 只，整轮同步失败且不会领取计划；WebAPI 会独立执行相同数量门禁和 ST 名称复核；
- 不调用 BJSE，不复制或复用前一交易日股票池；任何日历分歧、字段缺失或数量异常都会阻止 `get_plan`；
- 成功后默认每 5 分钟重新同步一次。WebAPI 对内容相同的股票池幂等处理，同时可在状态表丢失后自动修复。

运行状态写入 `runtime/collector-state.json`，日志写入 `runtime/collector.log`（10 MB 轮转，保留 5 份）。这两个文件均不保存 Token 或 Gateway Key。
一个 Worker 结果中的成功清理、失败计数和第三次失败黑名单会合并为一次原子落盘。Windows 如果短暂拒绝替换状态文件，采集端会在 1.55 秒内做 5 次有界退避重试；仍失败则显式升级为致命故障并中止 cycle，不会忽略或伪造已持久化状态。

## 私有配置

复制 `config.example.json` 为 `config.local.json`。正式密钥仅放在这个已被 `.gitignore` 排除的私有文件，或者通过进程环境变量提供：

- `PAIR_TREND_GATEWAY_KEY` 优先于 `gatewayApiKey`；
- `ASTOCK_TOKEN` 优先于 `gmToken`。

任务计划的命令行只出现 Python、程序和配置文件的绝对路径，不携带密钥。

部署前执行只读校验：

```powershell
python .\main.py --config .\config.local.json --validate-config
```

该命令只验证配置、WebAPI 鉴权、gm SDK 初始化以及最近已收盘交易日的只读 `history` 权限，不领取计划、不上传 K 线、不推进正式 cycle。

手工执行一个完整正式轮次：

```powershell
python .\main.py --config .\config.local.json --once
```

严格补算一个过去交易日（WebAPI 默认最多接受最近 7 天）：

```powershell
python .\main.py --config .\config.local.json --backfill-date 2026-08-17 --once
```

历史补算不会使用今天或前一日的 ST/停牌状态。采集端先用
`get_instrumentinfos` 按上市、退市日建立该日候选集，再分批调用
`get_history_instruments` 取得请求日精确的 `sec_level/is_suspended`；连续两轮
规范化快照完全一致后，才以 `dongcai-gm-history` 来源同步给 WebAPI。WebAPI
只用同日 `authoritative_universe_sync` 和 `instrument_daily_status` 生成全日
09:30～15:00 四周期计划，结果仍沿用内存回放和实时结果表幂等写入。

`--backfill-date` 必须与 `--once` 同时使用。正式执行前应暂停常驻计划任务，
补算提交后等待 WebAPI collection status 进入 `idle` 且无 `lastError`，核对结果
数量，再恢复常驻任务。补算失败状态单独写入
`runtime/backfill/YYYY-MM-DD/collector-state.json`，不会污染当日采集黑名单。

不要通过截断股票池进行冒烟测试；WebAPI 的 cycle 是全股票池原子协议，部分股票绝不能调用 `complete`。

## Windows 开机自启

在管理员 PowerShell 中执行：

```powershell
.\install_autostart.ps1 -PythonExe "C:\path\to\python.exe" -StartNow
```

安装脚本先运行 `--validate-config`，通过后注册 `AStockMonitor-PairKlineCollector` 登录任务。由于 gm SDK 依赖桌面版东财掘金终端的本地会话服务，任务必须与掘金终端使用同一个已登录 Windows 用户，不能使用 `SYSTEM`。任务在该用户登录后延迟 90 秒启动，并拒绝重复实例；掘金终端任务应先启动。常驻 `run_collector.ps1` 包装器把四周期采集（1个Supervisor+6个Worker）和波段日 K 采集（1个进程）作为同一个运行单元。任一角色退出时会回收另一角色及遗留子进程，等待60秒后整体重启，不依赖任务计划程序可能失真的 `LastTaskResult`。程序路径、配置路径、工作目录和日志目录均会解析为绝对路径。无人登录时，桌面版掘金终端和采集器都不会运行，这是供应商终端的会话约束。

删除自启动项：

```powershell
.\uninstall_autostart.ps1
```

## 运维心跳

监督进程定期向 `/api/internal/operations/collector-heartbeat` 上报 6 Worker 状态、进程 PID、活动任务、排队股票、重试、失败、黑名单和已完成 cycle 数。心跳失败只影响可观测性，不改变采集完整性和 API 水位。
