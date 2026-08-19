# 正式部署清单（2026-08-18）

## 服务器职责

| 节点 | 地址 | 正式职责 | 不再运行 |
|---|---|---|---|
| Web 节点 | `<web-node-ip>` | WebAPI、网页、systemd | Docker、Redis |
| 数据库节点 | `<mysql-node-ip>` | MySQL | WebAPI、网页、采集程序 |
| 本机采集节点 | Windows 本机 | Python 监督进程、6 个采集 Worker | Redis/MySQL 直连、业务计算 |

## Web 节点路径

| 内容 | 正式路径 | 权限/所有者 |
|---|---|---|
| WebAPI 与网页发布目录 | `/opt/astock-monitor/api` | `root:astock 0750` |
| WebAPI 可执行文件 | `/opt/astock-monitor/api/AStockMonitor.Api` | `root:astock 0750` |
| 私密环境配置 | `/etc/astock-monitor/webapi.env` | `root:astock 0640` |
| 运行状态目录 | `/var/lib/astock-monitor` | `astock:astock 0750` |
| systemd 单元 | `/etc/systemd/system/astock-webapi.service` | `root:root 0644` |
| systemd 服务名 | `astock-webapi` | 开机自启 |
| 本次回滚版本 | `/opt/astock-monitor/releases/previous-20260818-134000` | 部署回滚保留 |
| 本次部署 staging | `/opt/astock-monitor/releases/staging-20260818-134000` | 原子切换完成后已移走，残留数 0 |
| 本机最终发布包 | `C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\.artifacts\astock-webapi-linux-x64-20260818-134000.tar.gz` | SHA-256 见本文末尾 |

WebAPI 直接在 `0.0.0.0:6379` 提供 HTTP API 和网页。端口号虽为 6379，
但 Redis 已卸载且程序配置 `LegacyRedisWorkers__Enabled=false`；监听该端口的是
`AStockMonitor.Api`。

## 采集端路径与进程

| 内容 | 正式路径/名称 |
|---|---|
| 采集程序 | `C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\collector\pair_kline_collector\main.py` |
| 私密配置 | `C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\collector\pair_kline_collector\config.local.json` |
| 状态与日志 | `C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\collector\pair_kline_collector\runtime` |
| 2.2.2 完整候选快照 | `C:\Users\Administrator\Documents\Codex\2026-08-13\new-chat\astock-monitor\.artifacts\pair-kline-collector-2.2.2-20260818-100000.zip` |
| 开机任务 | `AStockMonitor-PairKlineCollector` |
| Python | `C:\Users\Administrator\AppData\Local\Programs\Python\Python313\python.exe` |
| 掘金终端启动器 | `E:\dfcf\EastMoneyGoldminer\gmstarter.exe` |
| 掘金终端登录自启任务 | `AStockMonitor-EastMoneyGoldminer`（Administrator 登录后延迟 30 秒） |

操作系统会看到 1 个监督进程和 6 个采集 Worker，共 7 个 Python 进程；
运维页的“采集进程”口径只统计 6 个 Worker。

## 账号与密钥位置

正式明文凭据不写入本文档、源码、发布包、systemd 命令行、计划任务命令行或运维接口。

| 凭据 | 账号/用途 | 唯一正式保存位置 |
|---|---|---|
| MySQL 应用密码 | `astock_webapi`，仅 `astock_monitor` 数据库读写 | Web 节点 `/etc/astock-monitor/webapi.env` |
| Collector Gateway Key | Python 调用 WebAPI 内部采集接口 | Web 节点 `webapi.env` 与本机 `config.local.json` |
| 掘金 Token | Python 调用行情 SDK | 本机 `config.local.json` |
| SSH 密码 | 两台云服务器 root 运维 | 不写入项目文件 |

需要人工查看时，只能分别使用 Linux `root` 或 Windows 管理员权限读取上述受保护文件。

## 网络清单

- Web 节点入站：`22/tcp`（运维）、`6379/tcp`（HTTP 网站和 API）。
- Web 节点出站：访问 `<mysql-node-ip>:3306`。
- 本机出站：访问 `<web-node-ip>:6379` 和掘金 SDK 服务。
- Web 节点不安装 Docker/Redis；已开放的 `6379/tcp` 改由 WebAPI 监听。

## 部署与验收顺序

1. 依次执行迁移 `027_pair_trend_collector_operations.sql`、
   `028_authoritative_daily_universe_sync.sql` 和
   `029_pair_trend_grouped_query.sql` 和
   `030_pair_trend_query_projection.sql`。
2. 发布到版本化 staging 目录，校验 SHA-256 和权限后停服原子切换；保留上一版本用于回滚。
3. 安装最小权限 MySQL 连接配置、随机 Gateway Key，并强制 MySQL 客户端 TLS。
4. 启用并启动 `astock-webapi.service`。
5. 验证 `/health/live`、`/health/ready`、`/`、`/operations`、
   `/api/operations/status`。
6. 用独立 smoke collectorId 测试心跳和黑名单接口，随后删除对应两张表中的测试行。
7. 安装并启动 `AStockMonitor-PairKlineCollector` 开机任务。
8. 核对监督进程 1 个、Worker 6 个、心跳版本 2.2.2、队列和失败重排可见；
   确认当日股票池在 `get_plan` 前完成权威同步。
9. 删除服务器上的发布压缩包、迁移临时文件和本机部署中转凭据。

## 本轮已核验

- Docker 与 Redis 已从 Web 节点卸载；`6379` 当前仅由 `AStockMonitor.Api` 监听 HTTP。
- MySQL 迁移 027—030 已执行；运维表、黑名单表、权威股票池凭证表、
  V3 `root_5m_eob` 完整性约束、分组查询索引以及强一致查询投影表/三个镜像触发器均存在。
- 正式 MySQL 应用账号为最小权限账号 `astock_webapi`；当前 3 条应用会话均使用
  `TLS_AES_256_GCM_SHA384`，WebAPI 连接串已改为 `SslMode=Required`。
- 前端类型检查和生产构建通过。
- .NET Release 构建与 `linux-x64` 自包含发布通过。
- Python 39 项调度、股票池、ST、双快照、稀疏 K 线、瞬时断连、增量边界、
  状态原子落盘和故障边界测试全部通过；`main.py`、`test_main.py`、
  `verify_sparse_gm.py` 语法编译通过。
- 内存采集协议验证通过。
- Windows 任务为 SYSTEM、开机触发延迟 30 秒、失败每分钟重启；当前为 1 个监督进程加 6 个 Worker。
- 东财掘金量化终端使用厂商 `gmstarter.exe` 注册为 Administrator 登录触发任务，延迟 30 秒；
  当前 `emgm3` 与 `gmterm-serv` 进程均正常响应。GUI 终端不会在无人登录的 SYSTEM 会话中启动。
- 2026-08-18 正式股票池：5207 只有效沪深 A 股，其中 ST 203、停牌 5、
  可采集 4999；总数、可采集数、版本、来源和质量元数据全部守恒。
- `/health/live`、`/health/ready`、网站、运维页和状态接口均返回 200；
  新浏览器会话打开 `/operations` 显示“系统正常”，控制台无错误。
- Web 正式包已移除“实时工作台”“策略任务”“消息中心”的导航、路由和页面产物；
  根路径及旧地址统一跳转到“对子顶底”。
- 2026-08-17 对子顶底严格补算已完成：权威股票池 5206、eligible 4998、
  官方三次一致确认 10 个无成交 EOB，实际内存 K 线 304868；四周期水位均到 15:00。
  最终写入 `pair-trend-v3` 事件 33771、命中 63988、生命周期记录 71446，孤儿记录为 0。
- 2026-08-17 股票分组查询已在正式库验收：33771 个事件归为 4791 个股票组；
  外层分页无重复，组内按 `root_5m_eob DESC,id DESC`，15:00 边界数据包含；
  TOP/BOTTOM 筛选、截至结束日状态、当前状态及旧 `/live/events` 接口均正常。
- 股票分组查询 Phase 1 v2 与旧 SQL 对总数、分页、顺序及全部 DTO 字段逐项一致。
  8 月 17 日正式 A/B 中位耗时由 0.52s 降至 0.42s（降低 19.2%），60 日查询
  由 0.98s 降至 0.82s（降低 16.3%）；部署后 warm API 的两次 8 月 17 日请求
  分别约 0.286s、0.275s，两次 60 日请求分别约 0.775s、0.779s。
- 部署后 8 月 17 日分组接口返回 `total=4791`、`totalPages=240`、第一页 20 组；
  第 999 页返回 0 组但总数和总页数保持正确。新 SQL 已移除全量 `ROW_NUMBER`
  和独立 `COUNT(DISTINCT symbol)`，最新记录查询继续严格使用
  `root_5m_eob DESC,id DESC` 及 029 索引。
- 分组查询第二阶段已上线：`pair_trend_query_event` 仅保留分组筛选所需的窄字段，
  INSERT/UPDATE/DELETE 触发器在原事务内同步 after-image，不依赖不完整的 lifecycle/delta，
  也没有异步延迟或自动回退。首次回填 169996 条与源表逐字段一致；正式新写入后
  再验证时投影为 172113 条，旧/新查询均返回 20 行、4791 组，验证脚本 PASS。
- WebAPI 使用版本号绑定的有界单飞内存缓存：历史分组 TTL 60 秒、盘中分组 TTL 10 秒，
  仅缓存成功响应，每次正式计算提交后先增加 revision 使全部旧键失效。
  2026-08-17 正式接口冷查询为 0.391—0.555s，同一规范化键后续命中为 0.003—0.009s；
  60 日冷查询 1.069s，命中 0.004—0.005s，响应 SHA-256 在冷/热请求间一致。
- 网页首屏已将详情抽屉和 K 线/ECharts 改为按需加载，桌面/移动端仅挂载当前断点 DOM，
  并为 JS/CSS 预生成 Brotli/Gzip。正式主 JS 请求命中 `Content-Encoding: br`，
  传输体为 238645 bytes；哈希资源一年 immutable，根路径与所有 SPA HTML 为
  `no-cache,no-store,must-revalidate`。
- 分组展开行的首次子数据不刷新问题已严格修复：原因是赋值表达式返回了
  `reactive` 容器中的原始对象，接口回写绕过 Vue Proxy，只在开发者工具引发 resize 后被动显示。
  现在创建后必须从容器再读取 Proxy 才允许写入，没有人工派发 resize 的兜底；
  专项回归测试证明首次响应立即触发 watcher。正式展开接口验收为 4791 组、
  样本 `SHSE.600007` 返回 4/4 条且 symbol 全部一致。
- 本地错误状态已按精确对象清理：删除 1000 条 09:35 增量边界误黑名单、
  1694 条 09:40 误 failure 和残留临时状态文件；清理后 `failure=0`、
  `blacklist=0`、临时文件不存在。污染状态已先备份到
  `.artifacts/recovery/collector-state-boundary-pollution-20260818-095235.zip`。
- 云 MySQL 仅按同一 collector、09:35 边界错误原因和污染时间窗口精确删除
  1000 条误黑名单；清理后 `matching_remaining=0`、`active_remaining=0`，
  未扩大到其他采集器、原因、时间窗口或正式业务结果。
- Collector 2.2.2 bootstrap 覆盖 4999 只股票、接收 39992 根 K 线，
  于 10:10:44 完成并回到 `idle`。首次 incremental 接收 9998 根 K 线，
  即每股各含重叠的 10:05 和新增的 10:10 两根，内存净增 4999 根，
  于 10:14:54 完成并回到 `idle`。10:16:46 又接受第二次 incremental 的
  9998 根 K 线，内存再次净增 4999 根，随后正常回到 `idle`。
- 连续增量验收后已闭合周期水位为 `5m=10:15`、`30m=10:00`；最终内存为
  4999 只股票、49990 根 K 线，`cyclesCompleted=3`。重试、失败、黑名单均为 0，
  `activeCycle=null`、`lastError=null`、`recentErrors=0`。
- Collector 2.2.2 于 10:07:24 启动后，`ERROR`、`CRITICAL`、`Traceback`、
  `RemoteDisconnected`、`非计划EOB`、`PermissionError/WinError 5` 和状态文件重试
  告警均为 0。09:52:38 的旧版 WinError 5 遗留错误字段已在确认当前心跳为
  2.2.2、`idle`、6 个 Worker、无活动周期且无黑名单后精确清除；只更新 1 行，
  清理后 `recentErrors=0`，未修改任何顶底事件、K 线或策略结果。
- 正式 WebAPI 当前 systemd PID 855101，`ActiveEnterTimestamp` 为
  2026-08-18 13:41:22 CST；`/health/live`、`/health/ready`、网站、历史数据页面及
  新分组接口均返回 200，systemd 保持 enabled，启动后 journal warning/error/exception 为 0。
  采集端已恢复 Running/Enabled，为 1 个监督进程+6 个 Worker，新启动日志未命中已知严重错误。
- 部署后首次 4999 股正式计算于 11:27:41 完成，内存为 124974 根 K 线，
  水位推进为 `5m=11:15`、`30m=11:00`、`60m=10:30`；随后增量周期正常接收，
  采集器心跳、6 Worker、重试/失败/黑名单和 API 就绪性均通过。
- 发布 staging 已清空，`/opt/astock-monitor/api` 为当前版本，
  `/opt/astock-monitor/releases/previous-20260818-134000` 保留用于 WebAPI 回滚；
  Web 节点两个上传包、DB 节点 030 迁移/验证脚本均已从 `/tmp` 精确删除，
  本机包、源码脚本和远端回滚目录仍可恢复。
- WebAPI 发布包 SHA-256：
  `69D9CAB607043FFF3D18BB607F687B780C4D632E3CE1D7BC3B8F02162A6A33D4`；
  `AStockMonitor.Api.dll` SHA-256：
  `E9698AFC425CE0A300BD96DC32800DCFA744E8D73AC6D37F8C27F79A27CA255F`。
- Collector 2.2.2 完整快照 SHA-256：
  `2C5BEAB5B45D3616A3E0D3CD342822717D8D46B35E3129EEA7118A3A9BC1B7CA`；
  其中 `main.py` SHA-256 为
  `1C73AB52F38A8305F7AC51012DFB944564E314E298C440C28FF31BAE2804E806`。
- 污染状态备份 SHA-256：
  `5EB9FAE71E608784EFF532DD4AB88759AA106AF6B04F6EBCE1F5553BA7131987`。

## 2026-08-18 当日日线误黑名单修复

- 故障根因：掘金 SDK 在收盘后尚未发布当日 `1d` K 线，旧采集器把整个周期/窗口空响应
  按 4999 个独立证券失败累计，三次后错误写入 24 小时黑名单。
- WebAPI 分钟 K 仍使用 90 秒宽限；当日日 K 改用独立
  `PairTrendCollection:DailyBarGraceSeconds=7200`，历史补算不受该宽限影响。
- Collector 2.2.5 将整组无有效计划行升级为供应商频率级故障：周期显式 abort、
  个股失败计数和黑名单上报均不变化，并使用 300 秒退避后重试；退避期间继续按心跳
  周期上报 degraded，避免活进程被判 offline。仍禁止合成日线或把整组空响应证明为无成交。
- 采集运维总状态不再在 collection failed、采集器 offline 或活动黑名单存在时误报
  healthy；健康心跳会显式清空旧 `last_error/last_error_at`，避免已恢复后仍展示过期错误。
- 本地状态先备份至
  `.artifacts/recovery/collector-state-daily-bar-blacklist-20260818-162201.zip`
  （SHA-256 `DAAA98C84292C48D193A81506FC7E088CBF86A9E03410AE368DDD2D1AF902646`），
  再精确删除同一日线原因的 4999 项；清理后 failure=0、blacklist=0。
- MySQL 事务清理同时限定 collector、07:07—07:18 UTC、`1d`、expected/actual及
  15:00 EOB，门禁要求恰好 4999 行且只有一个原因；实际 matched=4999、deleted=4999、
  active_remaining=0。
- 修复后正式重建为 4999 只、299931 根分钟 K 线，水位
  `5m/30m/60m=15:00`，于 16:56:53 回到 idle；9 个5分钟无成交空档均有官方三次一致证明。
- 17:00 正式验证掘金当日日线仍返回空：Collector 2.2.5 中止周期并进入300秒退避，
  65秒后心跳年龄仍为1秒、6个Worker存活、黑名单仍为0，证明供应商级故障不再污染
  任何股票状态且退避不会制造离线误报。
- Collector 2.2.5 Python 42项测试全部通过；`main.py` SHA-256：
  `A4E612F29E27C00C337669601678B5611918190764F39E6BB1546223D73A1080`。
- 最终 WebAPI 包 `.artifacts/astock-webapi-linux-x64-20260818-164048.tar.gz`，SHA-256：
  `5082F018094F21ACDE41284BA86E0CF2ABA9790CDA74B45BFAC9CC2F57D94118`；
  systemd 于 16:41:59 CST 启动，PID 856220，live/ready均为200并保持enabled。

## 2026-08-18 GM Token 1026 与采集自恢复修复

- 17:30 后 GM `history` 返回 `status=1026/更新令牌错误`。旧 Worker 把共享鉴权异常
  复制成个股失败，最终本机污染 1000 条黑名单和 3199 条 failure；云端收到 999 条，
  另 1 条在 WebAPI 公网连接超时时未上报。随后批次推送遇到 `WinError 10060`，
  Supervisor 退出，而任务计划程序仍记录成功结果，没有触发原有失败重启。
- Collector 2.2.6 新增结构化 GM 鉴权分类。`1026` 及明确的 Token 无效/过期错误
  统一作为供应商级故障整轮熔断、保持 degraded 心跳和 300 秒退避，不修改任何个股
  failure/blacklist。启动及每 5 分钟使用最近已收盘交易日的小段官方 `history` 做只读
  权限预检；正式私有配置于 18:00 和部署时均通过。
- 幂等的 WebAPI 计划、股票池、批次、黑名单和心跳请求对连接超时/重置/远端断开
  执行 1、2、4 秒有限重试；HTTP 业务错误和 JSON 协议错误不重试。API 端既有
  `sourceRowHash` 同值幂等、异值冲突门禁保持不变。
- 本机污染状态清理前备份至
  `.artifacts/collector-auth-fix-20260818-180019/collector-state.json`；只删除原因匹配
  `1026/更新令牌错误` 的 1000 条 blacklist 和 3199 条 failure，清理后均为 0。
  云 MySQL 只删除 `collector_id=local-pair-kline-01`、09:30—09:34 UTC 且同一原因的
  999 行，`deleted=999`、全表和活动黑名单均为 0，没有扩大到其他时间或原因。
- `AStockMonitor-PairKlineCollector` 已改为 SYSTEM 任务内的常驻
  `run_collector.ps1` 包装器。Supervisor 退出时，包装器用精确父 PID 回收6个旧
  ProcessPool Worker，等待60秒再启动新进程，避免依赖失真的 `LastTaskResult`。
  故障注入先后暴露并修复了孤儿 Worker 和 `Start-Process -Wait` 等待整棵进程树问题；
  最终验收为旧1+6全部清零、退避期无重叠进程、随后自动恢复新的严格1+6。
- 18:03:59 正式全量周期接收 4999 只股票、304930 根 K 线，其中分钟 K 线
  299931 根、日 K 线 4999 根；9个分钟空档均有三次官方一致稀疏证明。18:17:27
  完成计算并回到 idle，四周期水位均为 `2026-08-18 15:00:00`。
- 最终故障注入恢复后运维状态：overall/collector 均 healthy、版本 2.2.6、6个Worker、
  collection=idle、activeCycle=null、retry/failure/blacklist/recentErrors 均为0，
  内存仍为4999只/304930根，四周期水位保持15:00。
- Python 49项测试全部通过，PowerShell包装器语法校验和真实故障注入通过。
  `main.py` SHA-256：
  `68786AF1D940C981891715DD322A6FEE445B6CB809AA970A3A84C800E3988245`；
  `run_collector.ps1` SHA-256：
  `9F481812E5933EBF7A66D4117BACB542B878DB51AE4A427DD3D4E99D5AA6B192`；
  无私密配置的完整快照
  `.artifacts/pair-kline-collector-2.2.6-20260818-1800.zip` SHA-256：
  `07CF0A5E9726C68AC3C593D2B1992D8D380D9CF8DD34DFC3BA33D0FD314DED1B`。

## 2026-08-19 掘金终端跨会话导致当日未扫描修复

- 直接原因：开机后的 Collector 2.2.6 运行在 Windows Session 0 的 `SYSTEM` 任务中，
  东财掘金终端及 `gmterm-serv` 运行在 Administrator 的交互式 Session 1。GM
  `get_trading_dates` 每20秒返回 `status=1001/无法连接到终端服务`，因此采集器始终停在
  `validating_provider`，没有同步 8 月 19 日股票池，也没有领取扫描计划。
- 同一配置在 Administrator Session 1 执行只读 `--validate-config` 实测通过，证明 Token、
  WebAPI 和供应商历史接口本身正常，故障边界是 Windows 会话隔离，不是行情缺失或鉴权1026。
- `AStockMonitor-PairKlineCollector` 已改为 Administrator 的 `Interactive` 登录任务，登录后
  延迟90秒启动；东财掘金任务仍先在登录后30秒启动。任务为 enabled/running，触发器用户为
  `BF-202510251713\Administrator`。无人登录时两者均不运行，符合桌面终端的会话约束。
- Collector 2.2.7 将结构化 `status=1001` 且消息为“无法连接到终端服务”分类为供应商级
  `ProviderTerminalUnavailableError`：保持 degraded 心跳、300秒退避、不累计个股失败或黑名单，
  并在运维状态显示明确错误；其他1001错误仍保持普通严格失败，避免过度分类。
- 任务切换时发现任务计划程序停止旧包装器后可能遗留 Python 进程树。安装脚本现会在停止旧任务前
  按 runner/main/config 绝对路径精确锁定进程树并清理；本次已精确终止旧 Session 0 的1个
  Supervisor、6个Worker及其conhost，保留 Session 1 的新实例。
- 正式恢复结果：10:03:49 启动严格1个Supervisor+6个Worker；10:03:50历史权限预检通过；
  10:03:55同步 8 月 19 日权威股票池 `total=5208/eligible=4999`；首轮提交4999只、
  34993根已闭合K线并在10:07完成，水位 `5m/30m=10:00`。随后增量净增4999根，
  10:10:48回到idle并将5分钟水位推进至10:05；失败数、活动黑名单和业务错误均为0。
- Python 51项测试和py_compile全部通过；`main.py` SHA-256：
  `C1BD9852C66491A33AEEE100CD6C4FB1C69B321F73AA63CBC469E112A08EEF7A`；
  `install_autostart.ps1` SHA-256：
  `59D7113BD2E6244D39EC6753F0F3662654DA179E50E92FBEABB0FAB4CB0FD7E4`；
  无私密配置快照 `.artifacts/pair-kline-collector-2.2.7-20260819-1003.zip` SHA-256：
  `62413D95A95639FAE81031694812603BC258FE1A7ED9BCCE47DE78A15C17D1E0`。

## 2026-08-19 波段筛选与分数排序

- 盘中实时、历史分组和历史事件列表新增“全部波段信号 / 有信号 / 候选 / 强确认”筛选，
  并新增“顶底日期倒序 / 波段分数升序 / 波段分数降序”。筛选严格使用持久化
  `wave_signal`，不允许按分数阈值反推正式信号。
- 明细列改为“波段分数”，完成计算时只显示数字；未计算、数据不足和顶部事件显示 `—`，
  页面不再显示“无信号”“底部候选”“较强确认”等文字。股票组增加“最高波段分数”。
- 分数排序在数据库分页前执行；股票组按筛选结果中的最高有效分数排序，同分时继续使用
  `LatestPivotAt DESC,symbol ASC`，组内事件使用
  `wave_score,root_5m_eob DESC,id DESC` 的稳定次序。默认不传新参数时保持原日期排序。
- 迁移 `034_pair_trend_wave_query.sql` 已应用：强一致查询投影新增波段状态、信号、分数、
  算法版本和 revision，三个镜像触发器已重建。迁移后投影与正式事件表波段字段差异为 0；
  新增投影与正式表波段查询索引，正式 `EXPLAIN ANALYZE` 命中覆盖索引，60 日候选聚合
  扫描 282 条、生成 247 组，执行约 3.32ms。
- 正式 60 日接口验收：候选 247 个股票组、强确认 25 个股票组、有信号 310 条事件；
  分数降序第一页从 75 到 65，升序第一页从 60 开始，顺序校验通过。8 月 17 日默认查询
  仍为 4791 组、240 页、第一页 20 组；非法信号或排序字段均返回 400。
- Web 前端正式产物确认包含筛选、升降序和纯分数列，且不包含旧三段信号文字。
  WebAPI 包 `.artifacts/astock-webapi-linux-x64-20260819-230557.tar.gz` SHA-256 为
  `5ACF620929A68B3087FC8E4D50C2C02CA3E041B43D7B80D2767A901F3F1E72B2`；
  正式 DLL SHA-256 为
  `15022E6D361890D27E1CFCD607B7CFC81F03454CD8BBF9AFD9A1AC1B15F574CD`。
- systemd 于 23:11:47 CST 启动，PID 862745，保持 active/enabled；live、ready、网站和
  MySQL 均为 healthy，启动后 warning/error/exception 为 0。回滚目录为
  `/opt/astock-monitor/releases/previous-20260819-230557`。
- 恢复采集时发现一个 22:28 启动的旧孤儿进程树与新任务并存；WebAPI 的单周期门禁阻止了
  并发提交，未产生重复业务写入。已按项目绝对路径精确清除两套进程树并只启动一套，最终
  进程模型为 1 个任务包装器、1 个主采集、6 个 Worker、1 个波段 Worker。
- API 重启后的正式全量周期于 23:27:00 回到 `idle`：4999 只股票、304928 根 K 线，
  5m/30m/60m/1d 四周期水位均为 15:00，失败、黑名单和最近错误均为 0。波段任务
  15043/15043 全部 `COMPLETED`，重建后的投影差异仍为 0；两台服务器的上传临时文件
  和 staging 均已删除，正式回滚目录保留。

## 2026-08-20 波段v3评分明细与全量重算

- 正式算法升级为 `pair-wave-bottom-v3`。原有六个计分项保留，新增
  “突破短期压力并站上5日线”10分项：最新已闭合日K收盘价必须同时高于此前10个
  交易日最高价和当日MA5。趋势门禁作为0分审计项写入明细；总分100，候选阈值70，
  强确认阈值85。
- 详情接口新增结构化 `waveScoreBreakdown`，返回总分、满分、信号、趋势门禁、算法版本、
  数据截止时间，以及每项的实际得分/满分/是否命中/指标证据。API严格校验逐项实际得分
  之和等于 `wave_score`；列表分数改为可点击，桌面与移动端均复用详情抽屉展示评分项。
- 迁移 `035_wave_bottom_v3_rescore.sql` 已应用。迁移前v2完成任务和v2物化结果均为15043；
  迁移创建15043个v3持久任务，不提前删除v2结果。单独波段Worker按每批最多200条逐项
  原子替换，最终v3任务15043/15043 `COMPLETED`，待处理/租约/失败均为0，v2物化结果为0。
- v3最终分布：候选68条（70～75分）、强确认14条（均为85分）、已完成但未形成信号
  14855条、数据不足106条。正式投影与事件表的波段状态、信号、分数、算法版本和修订号
  差异为0。
- 正式强确认样本事件82288通过详情验收：85/100、8个明细项、实际得分合计85、趋势门禁
  通过、算法版本v3；新增项命中10/10，证据为收盘33.600、前10日压力33.280、MA5
  31.214。旧v2详情兼容样本也通过，重算过渡期可正确显示45/90和6个旧评分项。
- 首次发布包暴露MySQL保留字别名 `Signal` 导致详情500，采集任务尚未恢复且未领取任何v3
  任务时即被冒烟发现；别名改为 `WaveSignal/WaveScore` 后重新构建、切换并复验通过。
  最终包 `.artifacts/astock-webapi-linux-x64-20260819-235330.tar.gz` SHA-256为
  `E6CC83C1F4877EB3A7A6D93FE714D45A5AA3F30C1CEFEDEE9B5E1374CD7DC3B8`，正式DLL SHA-256为
  `BF67C8A2D6A25BAF798F8B83CF8D7D4B66AA65518324CD65DF1812B12A54E6CA`。
- systemd最终于2026-08-19 23:54:50 CST启动，PID 863271，保持active/enabled；有效回滚
  目录为 `/opt/astock-monitor/releases/previous-20260819-234618`。采集端恢复为唯一一套
  包装器、主采集、6个SDK Worker和1个波段Worker，重算日志新增错误为0。

## 本次回滚门禁

- 030 投影表与触发器与旧 WebAPI 兼容，但已通过正式强一致写入验证，日常不回退迁移。
  WebAPI 若验收失败，停止服务后将当前目录移出，
  再把 `/opt/astock-monitor/releases/previous-20260818-134000` 原子恢复为
  `/opt/astock-monitor/api`；私密环境文件保持在 `/etc/astock-monitor/webapi.env`，
  不随程序目录回滚。
- Collector 2.2.0 已确认存在 `RemoteDisconnected` 导致常驻进程退出、增量起点 EOB
  导致约 1000 只股票被错误加入黑名单两项生产缺陷，禁止恢复为业务运行版本。
- 每次 Collector 新候选必须在部署前保存不含 `config.local.json` 的完整源码快照并记录
  SHA-256。若 2.2.2 后续失败，立即停止采集，WebAPI 继续提供网站和查询接口；定位后
  以新版本修复前滚，不允许现场手工套反向补丁或恢复 2.2.0。

## 尚需运维加固

- 当前采集链路按用户选择使用公网 HTTP 6379，Gateway Key 在链路上没有 TLS 加密。
  后续应在同一端口启用 HTTPS，或通过可信 VPN/隧道传输。
- MySQL 公网 3306 的累计失败认证计数仍在增长。不要关闭应用连接；应在云安全组中
  只允许 Web 节点 `<web-node-ip>` 和明确的管理员出口 IP 访问 3306。
- MySQL 客户端已经强制 TLS，但服务端全局 `require_secure_transport` 仍为 OFF；
  确认其他客户端均支持 TLS 后，可在维护窗口统一开启。
