# 8策略逐时点历史回放与阈值校准

## 回放口径

- 数据源仅使用已经固化到 MySQL 的东方掘金官方 K 线，不调用交易、账户或下单接口。
- Fast 策略在每根已闭合官方 5 分钟 K 线结束时评估。
- Observe 和 Event 策略在每根已闭合 30 分钟 K 线结束时评估。
- 日线特征只能读取观察交易日之前的数据；当前交易日价格、成交量、成交额和 VWAP 由截至观察点的 5 分钟线累计构造。
- 30 分钟特征只能读取 `eob <= observed_at` 的已闭合桶。
- 同一股票、策略、交易日分别记录 75、80、85、90、95 分的第一次穿越，不使用当日收盘后的最高分反推盘中信号。

分钟行情回放范围限制为最近 60 个自然日且不含当天。回放自动裁剪到真实可用区间，并在 `strategy_replay_run.data_limitations` 中固化限制，不把范围外区间视为零收益或完整数据。

## 未来表现与校准

每次阈值首穿分别计算：

- D1、D3、D5：第 1、3、5 个后续交易日收盘相对命中价的收益；
- W1：观察日七个自然日后第一个可用交易日收盘收益；
- MFE5、MAE5：后续五个交易日内最大有利和最大不利波动。

交易日按时间顺序切分为 70% 训练集和 30% 验证集。建议阈值必须同时满足：

1. 训练集和验证集有效 D3 样本均达到最小样本数；
2. 两个分段的 D3 平均收益均大于 0；
3. 两个分段的 D3 胜率均不低于 50%；
4. 对训练/验证目标分差异施加稳健性惩罚后排名最高。

校准只写入建议，不自动修改 `strategy_definition` 或线上规则。全市场回放后仍需至少五个交易日的影子运行。

## 数据库

迁移文件为 `database/migrations/009_strategy_historical_replay.sql`：

- `strategy_replay_run`：回放范围、版本、数据限制和总进度；
- `strategy_replay_symbol`：股票级断点和错误；
- `strategy_replay_signal`：每个阈值的盘中首次穿越证据；
- `strategy_replay_outcome`：D1/D3/D5/W1、MFE5、MAE5；
- `strategy_calibration_result`：训练、验证、全样本指标和建议阈值。

## 执行

```powershell
dotnet run --project .\src\AStockMonitor.StrategyScanner\AStockMonitor.StrategyScanner.csproj `
  -c Release -- `
  --historical-replay `
  --start 2026-01-01 --end 2026-08-13 `
  --workers 4 `
  --thresholds 75,80,85,90,95 `
  --minimum-samples 30
```

可选参数：

- `--symbol-limit`：只回放前 N 只股票，用于验收；
- `--train-ratio`：训练集占比，默认 0.70；
- `--force`：创建新任务；不指定时同一参数任务可从失败股票断点续跑；
- `--allow-incomplete-data`：只允许开发验收使用，跳过回填、聚合和质量水位门禁。

## API

```text
GET /api/strategies/replay-runs?page=1&pageSize=50&status=
GET /api/strategies/replay-signals?runId=3&page=1&pageSize=50&strategyCode=&symbol=&threshold=
GET /api/strategies/replay-runs/{runId}/calibrations
```

每次完成后还会生成 `.artifacts/strategy-replay-{runId}.md` 校准报告。
