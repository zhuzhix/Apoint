# 2021 日K对子与波段底部离线回测

该工具只通过东方财富 GM SDK 读取历史行情，并把快照写入本机目录。代码中没有
WebAPI、MySQL 或 Redis 客户端，也不会执行数据库迁移。它用于在正式库没有历史
对子事件时，重新构造 `pair-trend-daily-v1` 日K对子事件。

## 日K口径

- 日K最高价命中 `.00/.11/.22/.../.99` 时生成顶部事件。
- 日K最低价命中相同对子尾数时生成底部事件。
- 相同股票、方向和价格在严格突破前保持同一事件；顶部被更高日K最高价突破、
  底部被更低日K最低价突破后失效。失效以后再次命中同价会进入下一 generation。
- 波段评分只对底部事件计算。信号在15:00日K完成后形成，评分可以使用该根已完成
  日K，回测收益从下一根可交易日日K开盘开始。
- 日K对子算法单独标记为 `pair-trend-daily-v1`，不冒充依赖5m/30m/60m/1d升级的
  `pair-trend-v3`。

## 执行

```powershell
$python = 'C:\Users\Administrator\AppData\Local\Programs\Python\Python313\python.exe'
$output = '.\.runtime\offline-wave-backtest\2021-01_2021-10'

& $python .\collector\offline_wave_backtest\main.py run `
  --daily-only `
  --config .\collector\pair_kline_collector\config.local.json `
  --output $output `
  --date-from 2021-01-01 `
  --date-to 2021-10-31 `
  --workers 6

dotnet run --project .\tests\DailyPairWaveBacktest\DailyPairWaveBacktest.csproj `
  -c Release -- `
  --input $output `
  --output "$output\results"
```

下载器逐日保存权威股票资格快照，按20只股票生成可断点续跑的 gzip 批次，并为每个
批次保存 SHA256。回测程序会重新验证资格文件、所有批次哈希、批次数和证券数守恒；
任何一项不一致都会拒绝计算。
