# 对子顶底事件详情开发交付记录

> 交付日期：2026-08-14  
> 状态：已开发、已部署、已完成接口与浏览器验收

## 1. 已交付功能

### 后端

- 新增实时详情接口：`GET /api/pair-trends/live/events/{id}`；
- 历史详情接口 `GET /api/pair-trends/events/{id}` 增加来源、回放运行和推荐 K 线窗口；
- 两个接口均支持 `hitPage`、`hitPageSize`，单页上限 200；
- 不存在的实时/历史事件返回 HTTP 404；
- 验收运行、`acceptance-fixture` 和 `TEST.*` 自动标记为非真实行情；
- 详情接口只读，不触发对子重算，不改变事件状态。

统一响应包含：

```text
source
sourceInfo
pairEvent
hits
recommendedChart
```

### 前端

- 列表“查看”打开事件详情抽屉；
- 抽屉状态写入 URL：`/pair-trends?source=history&eventId=10`；
- 刷新、前进、后退均可恢复同一事件；
- 独立详情页：`/pair-trends/{live|history}/{eventId}`；
- 展示事件摘要、评分、趋势强度、周期共振、命中统计、明细和审计来源；
- 验收样本显示醒目警示，并禁止请求真实官方 K 线；
- 正式股票支持 5m、30m、60m、1d 官方 K 线复核；
- K 线支持候选、确认、失效、撤回、当前选中标记；
- 命中明细行与图表周期、标记联动；
- “查看个股”保留 `pairSource` 和 `pairEventId` 上下文；
- 个股页可加载对应事件并将命中标在原有 K 线上。

### 实时刷新

- 实时详情订阅 `/hubs/market`；
- 按股票调用 `SubscribeSymbols`，监听 `pairTrend.changed`；
- 收到生命周期变化后刷新详情和事件列表；
- 离开页面时取消股票订阅；
- 保留 30 秒 REST 刷新作为断线兜底；
- 历史详情不连接 MarketHub。

## 2. 构建问题整改

本次浏览器验收发现 `web/src` 曾残留 `vue-tsc` 生成的旧 `.js` 文件。Vite 对无扩展名导入优先解析旧 JS，造成新页面调用旧 API 客户端。

已完成：

- 删除 `web/src` 下全部编译生成的 `.js`；
- 在 `web/tsconfig.app.json` 设置 `noEmit: true`；
- 重新构建并确认源码目录不再产生 JS；
- 重新发布 Windows API 服务。

## 3. 验收结果

| 验收项 | 结果 |
|---|---|
| .NET 全解决方案构建 | 通过，0 警告、0 错误 |
| Vue 类型检查与生产构建 | 通过 |
| 历史详情接口 | 通过 |
| 实时详情 404 | 通过 |
| 验收样本识别 | 通过 |
| 抽屉 URL 刷新恢复 | 通过 |
| 完整详情深链接 | 通过 |
| 命中明细与审计信息 | 通过 |
| API/Worker/StrategyScanner 服务 | 全部 Running |

验收使用现有历史样本：`TEST.BOTTOM`，事件 ID `10`。当前 `pair_trend_live_event` 没有记录，因此实时详情接口已完成 404、契约和编译验证，真实实时事件 UI 联动需要下一次盘中出现事件时进行运行态验收。

## 4. 使用入口

- 对子列表：<http://127.0.0.1:5222/pair-trends>
- 当前验收详情：<http://127.0.0.1:5222/pair-trends/history/10>
- Swagger：<http://127.0.0.1:5222/swagger>

## 5. 未改变的范围

- 未修改对子算法规则和阈值；
- 未修改实时 K 线、历史 K 线和事件落库结构；
- 未写入或伪造实时对子事件；
- 未增加数据库迁移；
- 未接入交易和下单能力。
