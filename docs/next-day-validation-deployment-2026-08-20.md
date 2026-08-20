# 成立事件次日验证部署记录（2026-08-20）

## 业务口径

- 验证对象：前一交易日进入 `ESTABLISHED` 的正式 `pair-trend-v3` 事件。
- 顶部：次一交易日任一已闭合 5 分钟 K 线最高价严格大于对子价，事件失效。
- 底部：次一交易日任一已闭合 5 分钟 K 线最低价严格小于对子价，事件失效。
- 盘中尚未突破为 `MONITORING`；15:00 全日窗口完成后才终结为 `PASSED` 或 `NO_TRADE`。
- 采集端只负责官方 K 线采集与稀疏证明；判定、事件修订、审计和页面查询都在 WebAPI。
- 实时验证失败会阻止整轮 K 线水位提交，不允许部分成功或前值合成。

## 历史验证

- 迁移：`036_pair_trend_next_day_validation.sql`。
- dry-run：run 2，2026-02-25 至 2026-08-19，共 21,277 条；失效 1,958、通过 19,181、无成交 11、不适用 127、失败 0。
- apply：run 3，计数与 dry-run 完全一致，状态 `COMPLETED`。
- 历史列表和详情页已显示验证日期、状态、突破时间、突破价格和观测极值。

## 实时验证

- 迁移：`037_pair_trend_next_day_realtime.sql`，增加 `MONITORING` 状态。
- 采集端版本：`2.3.0`，正式进程模型为 1 个主采集监督进程、6 个采集池进程、1 个波段日线进程。
- 2026-08-20 首轮：4,999 只股票、304,905 根 K 线，四周期水位均为 15:00。
- 实时 run 4：总计 1,247；失效 458、通过 789、无成交 0、不适用 0、失败 0，状态 `COMPLETED`。
- 验证任务与 `pair_trend_live_event` 的状态/日期差异计数为 0。
- 样本事件 722492（SHSE.600020）：顶部 3.77，2026-08-20 14:30 以 3.78 突破，事件改为 `INVALIDATED`。

## 正式部署与验收

- WebAPI：`/opt/astock-monitor/api`，systemd `astock-webapi` 为 active/enabled。
- 当前进程：PID 867545，启动时间 2026-08-20 19:44:12 CST。
- 正式包：`next-day-realtime-hotfix-linux-x64-20260820-194312.tar.gz`。
- 包 SHA256：`74D061974DA093E556AEB69D93F859094742E56232F91030EE199C840D0D525D`。
- 主 DLL SHA256：`DDF8476495FE4F1F6C30535297CF17A7B2E70E229F2366247C0E93C56BAB7037`。
- 回滚目录：`/opt/astock-monitor/releases/previous-20260820-193750`。
- 网页时区修复采用静态资源原子切换，未重启 API、未清空内存水位；回滚目录为 `wwwroot-previous-20260820-200643`。
- 最终运维状态：overall/collector/API/website/MySQL 均 healthy，6 个采集池进程在线，retry/failed/blacklist 均为 0，recentErrors 为 0。
- WebAPI 热修复启动后的 journal 未命中 warning/error/exception/fail/deadlock/timeout。
- 浏览器实测历史分组展开和详情均显示次日验证；样本验证完成时间正确显示为北京时间 19:44:53。

## 验证命令结果

- `dotnet build AStockMonitor.sln -c Release --no-restore`：0 警告、0 错误。
- `PairTrendCollectionVerification`：通过（仅 NuGet 漏洞源 TLS 网络告警）。
- Python：56 项测试通过，`py_compile` 通过。
- Web：`vue-tsc` 通过，Vite 生产构建通过；仅保留既有的大 chunk 体积提示。

