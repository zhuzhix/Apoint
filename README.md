# A股监控程序

网页工作台已部署后可直接访问：`http://127.0.0.1:5222/`。API 基础状态迁移到 `http://127.0.0.1:5222/api/status`，Swagger 仍为 `http://127.0.0.1:5222/swagger`。

网页开发、通知链路和使用说明见 [docs/web-frontend-implementation.md](docs/web-frontend-implementation.md)。

Windows 环境下的 A 股实时行情与历史 K 线数据底座。

当前已实现：

- .NET 10 Web API、gRPC 实时入口、SignalR 行情推送；
- Python 东方财富掘金 SDK 数据采集；按当前实测会话上限每进程最多 50 只股票，Supervisor 自动稳定分片；
- Tick 只保存在每进程 SQLite Outbox、内存和 Redis 当日短期层，不写 MySQL；
- Redis 生成非正式 1 分钟盘中预览，服务 VWAP、量能和快速策略；
- 正式 `5m/30m/60m/1d` 全部直接采用 SDK 官方数据并幂等写入 MySQL；
- V2 `BarClosed/BarRevised` 可靠 Outbox、16 分片 Stream 和独立 Consumer Group；
- 按交易日变化的严格沪深 A 股、非 ST、非北交所股票池；
- 最近 60 自然日分钟 K 线与日线历史增量、断点续传、SDK 挂起看门狗；
- 启动、K 线边界、盘中滚动和收盘四类缺口检测与自动补数；
- 缺失、重复、OHLC、成交量、时段、来源和完整性检查；
- 包含 `.00` 的盘中对子顶底实时扫描、修订重算、多周期归并与分页 API；
- 底部事件达到 `FOCUS` 后，由独立 Python 进程按需采集此前 120 个交易日日 K，WebAPI 在内存中计算 0～100 波段信号并写回事件；
- 8 个策略的定时/事件扫描、生命周期、逐时点历史回放和阈值校准；
- Swagger UI、OpenAPI JSON、中文接口参数和响应字段说明；
- Grafana、Prometheus、Loki、Tempo、OpenTelemetry 中文运维监控；
- 每日增量流水线、分钟 K 线月分区、Parquet 年度归档和安全清理。

本项目只使用东方财富掘金终端获取数据，不调用交易、账户或下单接口。这里的“策略扫描”只生成研究信号和网页任务，不执行交易。

## 快速开始

启动 MySQL 和 Redis：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-data-services.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\apply-database-migrations.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-data-services.ps1
```

构建 .NET：

```powershell
dotnet build .\AStockMonitor.sln
```

启动独立策略扫描服务：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-strategy-scanner.ps1
```

运行 Python 测试：

```powershell
$env:PYTHONPATH = (Resolve-Path .\collector).Path
.\collector\.venv\Scripts\python.exe -m unittest discover -s .\collector\tests -v
.\collector\.venv\Scripts\python.exe .\collector\tests\verify_history_database.py
```

历史 K 线小样本回放：

```powershell
cd .\collector
.\.venv\Scripts\python.exe -m astock_collector.history.cli replay `
  --start 2026-01-01 --end 2026-08-13 `
  --workers 1 --symbol-limit 3 --validate-source-symbols 3
```

东方掘金Token从已忽略的 `collector/config.local.json` 读取，格式参考 `collector/config.example.json`。

对子趋势算法与接口验收：

```powershell
dotnet run --project .\src\AStockMonitor.Backtest\AStockMonitor.Backtest.csproj -- --self-test
dotnet run --project .\src\AStockMonitor.Backtest\AStockMonitor.Backtest.csproj -- --bar-self-test
dotnet run --project .\src\AStockMonitor.Backtest\AStockMonitor.Backtest.csproj -- `
  --start 2026-01-01 --end 2026-03-31 `
  --frequencies "5m,30m,60m,1d" --acceptance-fixture --force
```

`--acceptance-fixture` 只生成明确标识的非真实行情验收数据，不得用于投资分析。

开发环境启动 API 后，可打开：

- Swagger UI：`http://127.0.0.1:5222/swagger`
- OpenAPI JSON：`http://127.0.0.1:5222/swagger/v1/swagger.json`

当前本机部署已显式启用 Swagger，且 HTTP 仅监听 `127.0.0.1:5222`。拆分部署时应通过防火墙、反向代理或认证限制访问范围。

完整说明：

- [当前项目架构与功能执行流程（V4）](docs/system-architecture-and-flows-v4.md)
- [行情采集 V4 可执行改造方案](docs/market-data-collection-v4-execution-plan.md)
- [上一版系统架构（V3，留作变更对照）](docs/system-architecture-and-flows-v3.md)

- [开发环境启动](docs/getting-started.md)
- [历史 K 线数据底座](docs/historical-kline-foundation.md)
- [对子顶底 V3 与回测接口](docs/pair-trend-v3.md)
- [重点底部波段信号回测方案](docs/wave-bottom-backtest-plan.md)
- [盘中行情数据核心服务](docs/intraday-market-data-core.md)
- [行情缺口检测与自动补数服务](docs/market-gap-recovery.md)
- [运维监控平台部署说明](docs/observability-deployment.md)
- [Windows 开机自启](docs/windows-autostart.md)
- [策略扫描服务迁移与开发方案](docs/strategy-scanner-service-plan.md)
- [8策略逐时点历史回放与阈值校准](docs/strategy-historical-replay.md)
- [总体技术方案](../outputs/A股监控程序-技术方案与开发计划.md)
