# 开发环境启动说明

## 1. 依赖

需要安装：

- .NET 10 SDK；
- Python 3.11 或东方财富终端支持的版本；
- 东方财富掘金 Python SDK；
- MySQL 8.4；
- Docker Desktop 和 WSL2；
- Git。

验证命令：

```powershell
dotnet --version
python --version
docker --version
docker compose version
wsl --status
```

## 2. 启动 Redis 和 MySQL

在项目根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-data-services.ps1
```

默认服务：

- Redis：`127.0.0.1:6379`，密码 `change-me`；
- MySQL：`127.0.0.1:3306`，数据库 `astock_monitor`；
- MySQL 应用账号：`astock_app`，密码 `change-me`。

生产或共享环境必须通过参数传入新密码，不要使用默认密码：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-data-services.ps1 `
  -RedisPassword "替换为Redis密码" `
  -MySqlRootPassword "替换为MySQL root密码" `
  -MySqlAppPassword "替换为应用账号密码"
```

确认服务：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-data-services.ps1
```

MySQL 首次启动时会自动执行 `database/migrations/001_initial.sql`。已有数据卷不会重复执行初始化脚本。

## 4. 生成 Python gRPC 代码

```powershell
cd .\collector
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python -m venv .venv-proto
.\.venv-proto\Scripts\python.exe -m pip install -r requirements-dev.txt
powershell -ExecutionPolicy Bypass -File .\scripts\generate_proto.ps1 -Python .\.venv-proto\Scripts\python.exe
```

生成文件位于：

```text
collector/astock_collector/generated/
```

## 5. 配置采集器

```powershell
$env:ASTOCK_GRPC_TARGET = "127.0.0.1:7000"
$env:ASTOCK_OUTBOX_DIR = ".runtime/outbox"
```

Token从已忽略的 `collector/config.local.json` 读取，格式参考 `collector/config.example.json`。当前 Worker 是纯数据采集模式：Token仅用于连接东方掘金行情服务，不配置策略ID，也不调用下单、账户或策略执行接口。

启动单 Worker：

```powershell
python -m astock_collector.supervisor `
  --symbols SHSE.600000,SZSE.000001 `
  --workers 1
```

没有连接东方财富终端时，可先使用模拟行情验证 .NET gRPC 接入链路：

```powershell
python -m astock_collector.simulator `
  --target 127.0.0.1:7000 `
  --symbols SHSE.600000,SZSE.000001 `
  --count 3
```

启动多 Worker 分片：

```powershell
python -m astock_collector.supervisor `
  --symbols SHSE.600000,SZSE.000001,SHSE.600519,SZSE.300750 `
  --workers 2
```

## 6. 启动 .NET 服务

```powershell
cd ..
dotnet restore .\AStockMonitor.sln
dotnet build .\AStockMonitor.sln
dotnet run --project .\src\AStockMonitor.Api\AStockMonitor.Api.csproj
```

默认开发地址：

```text
http://localhost:5222
```

根地址现在打开 Vue 网页工作台。API 基础状态地址为：

```text
http://127.0.0.1:5222/api/status
```

gRPC 明文 HTTP/2 地址：

```text
127.0.0.1:7000
```

检查接口：

```text
GET /health/live
GET /health/ready
GET /api/market/latest
```

开发环境 Swagger：

```text
http://127.0.0.1:5222/swagger
http://127.0.0.1:5222/swagger/v1/swagger.json
```

Swagger UI 包含 REST 接口、查询参数、响应状态码、分页 DTO 和中文字段说明。gRPC 与 SignalR
不属于普通 HTTP OpenAPI 操作，因此它们的连接地址和方法仍以本文档及 Protobuf/Hub 代码为准。

生产环境默认不开放 Swagger。如确需临时启用：

```powershell
$env:Swagger__Enabled = "true"
```

生产开放时应同时限制网络来源或增加认证，不能把内部数据接口直接暴露到公网。

## 7. 启用持久化 Worker

开发环境验证 Redis/MySQL 后，推荐使用环境变量覆盖连接配置：

```powershell
$env:Market__PersistenceEnabled = "true"
$env:Market__RedisConnection = "localhost:6379,password=change-me"
$env:Market__MySqlConnection = "Server=localhost;Port=3306;Database=astock_monitor;User ID=astock_app;Password=change-me;SslMode=None;AllowPublicKeyRetrieval=True;"
```

然后运行：

```powershell
dotnet run --project .\src\AStockMonitor.Worker\AStockMonitor.Worker.csproj
```

Worker 启动时会使用 `XAUTOCLAIM` 接管超过 `PendingMessageIdleMs` 未确认的消息；只有 MySQL 批量事务提交后才执行 `XACK`。

## 8. 采集器运行与压力测试

采集器支持从文件读取股票列表、单 Worker 多股票和多个 Worker 分片。Token默认从本机忽略配置读取：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-collector.ps1 `
  -SymbolsFile .\collector\config\symbols.example.txt `
  -Workers 2
```

API 运行态接口：

```text
GET http://127.0.0.1:5222/api/market/runtime
```

该接口返回 Worker 连接状态、最后心跳、队列深度、采集/发布计数、CPU、内存及 API 接收计数。

100 标的合成全链路压力测试：

```powershell
$env:ASTOCK_LOAD_TEST_REDIS_URL = "redis://:change-me@127.0.0.1:6379/0"
& .\collector\.venv\Scripts\python.exe -m astock_collector.simulator `
  --symbol-count 100 --workers 4 --count 10 `
  --redis-url $env:ASTOCK_LOAD_TEST_REDIS_URL
```

## 9. 已知限制

当前代码已完成第一阶段数据底座和一次真实行情联调：

- Python `gm` SDK 3.0.186 已在东方财富量化终端环境中验证；`tick` 的 row 格式不支持 `pre_close`，归一化层会将该字段保留为 `null`；
- Python gRPC 代码需要先执行生成脚本；
- 数据源权限、订阅上限和 Tick 实际频率需要实测；
- Worker 当前默认关闭 Redis/MySQL 持久化；
- SignalR 当前已支持按股票 Group 推送，但前端尚未创建。
