# 云端 .NET 与本地采集 Gateway 部署

此版本只允许一条官方 K 线数据路径：本地 Python 调用 GM SDK，写入本地
`CollectorGateway` 命名管道；Gateway 将批次耐久保存后通过 TLS gRPC 发给云端
API；云端 .NET 批量落库并创建策略重算任务。Python 不拥有 MySQL、Redis 或云端
API 凭据。

## 云端

1. 在维护窗口按编号执行 `database/migrations/025_collector_gateway_strict_boundary.sql`
   和 `database/migrations/026_collector_tick_assignments.sql`。
2. 发布 API、Worker 和 StrategyScanner；API、Worker 的 `CollectorControl:GatewayId`
   必须与本地 Gateway 配置相同。
3. 在 API 的受保护配置中设置 `CollectorControl:GatewayApiKey`。不要将该值写入
   `appsettings.json`、源码、Python 配置或日志。
4. 以 HTTPS/HTTP2 暴露 API 的 gRPC 端点。云端无需、也不得反向开放本地终端端口。

## 本地东财终端

1. 发布 `AStockMonitor.CollectorGateway`，复制
   `appsettings.example.json` 为运行时配置；将 `ApiKey` 写入 Windows Credential
   Manager 或受保护的环境变量配置，不能保存到 JSON 文件。
2. 使用 `scripts/install-collector-gateway-task.ps1` 将 Gateway 配置为“用户登录后运行”
   的计划任务；它必须位于东财终端的交互式 Windows 会话，不能作为 LocalSystem 服务运行。
3. Python 环境仅保留 GM SDK、Token 和 Gateway 本机命名管道名。删除
   `ASTOCK_MYSQL_*`、`ASTOCK_REDIS_*` 与任何云数据库连接配置。
4. 网络只允许 Gateway 对云 API 的出站 `443/TCP`；拒绝 Python 运行账户访问
   云 MySQL `3306` 和 Redis `6379`。

## 切换门槛

- 迁移完成且 `collector_gateway` 显示 Gateway 在线。
- 启动一个受控历史命令，确认 `collector_command`、`collector_result_batch`、
  `official_bar_staging`、正式 K 线、`bar_event_outbox` 与 `strategy_replay_task`
  都具有同一 `command_id` 的可追踪记录。
- 删除旧的 Snapshot、Hot Tick、Recovery 和通用 Collector 计划任务后才允许打开
  `AStockMonitor-CollectorGateway`；两条路径不得同时运行。
- Gateway/API/Worker 任一重启后，只能从本地收件箱或云端命令恢复，不能启用
  Python 直连 MySQL、Redis 的旧程序。
