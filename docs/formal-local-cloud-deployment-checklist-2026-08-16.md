# 本地程序 + 云端存储正式部署清单（2026-08-16）

## 部署拓扑

- 本机：API、Worker、StrategyScanner、CollectorGateway、Python 纯采集子进程、掘金终端。
- 云 Redis：`<redis-node-ip>:6379`，Docker 容器 `astock-cloud-redis`。
- 云 MySQL：`<mysql-node-ip>:3306`，正式库 `astock_monitor`。
- API HTTP：`http://127.0.0.1:5222/`。
- API gRPC：`http://127.0.0.1:7000/`，仅允许本机明文 HTTP/2。

## 正式程序路径

- API：`deploy/services/AStockMonitor.Api/AStockMonitor.Api.exe`
- Worker：`deploy/services/AStockMonitor.Worker/AStockMonitor.Worker.exe`
- StrategyScanner：`deploy/services/AStockMonitor.StrategyScanner/AStockMonitor.StrategyScanner.exe`
- CollectorGateway：`deploy/collector-gateway/AStockMonitor.CollectorGateway.exe`
- Python：`collector/.venv/Scripts/python.exe`
- Gateway inbox：`C:\ProgramData\AStockMonitor\CollectorGateway\inbox`

## 凭据与配置存放位置

安全起见，本清单不把明文密码提交到项目文件。实际值在本次部署交付消息中给出。

- API/Worker/StrategyScanner：
  `HKLM\SYSTEM\CurrentControlSet\Services\<服务名>\Environment`
- Gateway API Key：
  `AStockMonitor.Api` 服务环境中的 `CollectorControl__GatewayApiKey`
- Gateway 启动脚本会在启动时读取上述 Key，仅注入 Gateway 进程；不写 appsettings。
- Redis Docker：`/opt/astock-redis/.env`
- Redis Compose：`/opt/astock-redis/docker-compose.yml`

## 启动与自启

- Windows 服务：`AStockMonitor.Api`、`AStockMonitor.Worker`、`AStockMonitor.StrategyScanner`，启动类型 Automatic。
- 计划任务：`AStockMonitor-Autostart`、`AStockMonitor-CollectorGateway`、`AStockMonitor-Goldminer`。
- `scripts/start-windows-autostart.ps1` 只等待云端 MySQL/Redis，不再启动本机数据容器。
- `scripts/start-collector-gateway.ps1` 从 API 服务环境读取认证 Key，并设置正式本机 API/gRPC、Python 和 inbox 路径。
- Redis 容器重启策略：`unless-stopped`。

## Redis 正式限制

- `maxmemory 1536mb`（1.50 GiB）
- `maxmemory-policy noeviction`
- Tick Stream 硬保留 3 分钟
- Retention 周期 15 秒
- 64 个固定 Tick 分片直接执行 `XTRIM MINID ~`

## 验收项目

- `GET /health/live`：HTTP 200。
- `GET /health/ready`：HTTP 200。
- 30 秒严格冒烟：600/600，数量守恒 PASS。
- 3 分钟保留冒烟：硬裁剪生效、inbox 排空。
- 测试数据：已从 Redis Stream 和预览 key 删除，测试 inbox 为 0。
- Release 构建：0 warning、0 error。

## 尚需管理员执行的一次操作

状态接口 Guid 映射修复已经构建并验证，但正式 API 文件尚未替换。管理员需要停止 `AStockMonitor.Api`，把 `.runtime/api-status-fix-publish` 覆盖到 `deploy/services/AStockMonitor.Api`，再启动服务。替换后验证：

```powershell
Invoke-WebRequest http://127.0.0.1:5222/api/market-collection-v4/status -UseBasicParsing
Invoke-WebRequest http://127.0.0.1:5222/health/ready -UseBasicParsing
```
