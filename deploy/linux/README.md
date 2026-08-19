# Linux WebAPI 与网页部署

- 程序目录：`/opt/astock-monitor/api`
- 私密环境文件：`/etc/astock-monitor/webapi.env`（`root:astock 0640`）
- 可写状态目录：`/var/lib/astock-monitor`
- systemd 源文件：`deploy/linux/astock-webapi.service`
- systemd 安装路径：`/etc/systemd/system/astock-webapi.service`
- HTTP：`0.0.0.0:6379`（仅复用已开放端口，进程仍是 WebAPI，不是 Redis）
- 网页：由 WebAPI 的 `wwwroot` 同源托管
- MySQL：远程云 MySQL，使用独立最小权限账号 `astock_webapi`
- MySQL 客户端：正式连接串使用 `SslMode=Required`，拒绝明文降级
- Redis：不安装、不连接；`LegacyRedisWorkers__Enabled=false`。端口号 6379
  只承载 HTTP 网站与 API。
- 采集鉴权：API 与本机 Python 共用一枚随机 Gateway Key

## 凭据边界

- MySQL 密码只存在于服务器 `/etc/astock-monitor/webapi.env`。
- Gateway Key 只存在于服务器 `webapi.env` 和本机采集端的
  `collector/pair_kline_collector/config.local.json`。
- 掘金 Token 只存在于采集端 `config.local.json`。
- 上述文件不得进入发布包、源码库、systemd 命令行、计划任务命令行或运维接口。
- 需要人工查看时，必须分别以 Linux `root` 或 Windows 管理员身份读取受保护文件；
  运维页面不会展示任何凭据明文。

常用命令：

```bash
systemctl status astock-webapi --no-pager
journalctl -u astock-webapi -n 200 --no-pager
curl -fsS http://127.0.0.1:6379/health/live
curl -fsS http://127.0.0.1:6379/health/ready
curl -fsS http://127.0.0.1:6379/api/operations/status
```

正式环境文件不得放进发布包或版本库。数据库密码与采集 Gateway Key 只能写入
`/etc/astock-monitor/webapi.env`，并限制为 `root:astock 0640`。
