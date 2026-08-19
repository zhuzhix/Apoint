"""Offline GM market-data snapshot builder for historical wave-bottom backtests.

This executable has deliberately no WebAPI, MySQL or Redis client. It reads only
the GM token from the collector's private config, downloads official point-in-
time market data, and writes gzip JSON/TSV artifacts below a local directory.
It never imports the live collector because that module owns WebAPI write paths.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import date, datetime, time as clock_time, timedelta
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import time
from typing import Any, Iterable, Sequence


ALGORITHM_VERSION = "pair-trend-v3"
WAVE_ALGORITHM_VERSION = "pair-wave-bottom-v3"
SNAPSHOT_VERSION = "offline-gm-v1"
STRICT_A_SHARE = re.compile(
    r"^(SHSE\.(600|601|603|605|688)|SZSE\.(000|001|002|003|300|301))[0-9]{3}$"
)
FREQUENCIES = {"5m": "300s", "30m": "1800s", "60m": "3600s", "1d": "1d"}
FIELDS = "symbol,bob,eob,open,high,low,close,pre_close,volume,amount"
HISTORY_INSTRUMENT_BATCH = 3_000
DEFAULT_SYMBOL_BATCH = 20
MIN_HISTORICAL_SYMBOLS = 3_000
MAX_HISTORICAL_SYMBOLS = 6_000
RETRY_DELAYS = (2.0, 5.0, 10.0)


class OfflineBacktestError(RuntimeError):
    """A strict data or isolation gate failed."""


@dataclass(frozen=True)
class CalendarRange:
    target_dates: tuple[date, ...]
    daily_dates: tuple[date, ...]
    daily_from: date
    daily_to: date


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build a local-only GM snapshot for pair-wave historical replay."
    )
    parser.add_argument("command", choices=("probe", "prepare", "download", "run"))
    parser.add_argument("--config", required=True, help="Private collector config; only gmToken is read.")
    parser.add_argument("--output", required=True, help="Local output directory.")
    parser.add_argument("--date-from", default="2021-01-01")
    parser.add_argument("--date-to", default="2021-10-31")
    parser.add_argument("--symbols-per-batch", type=int, default=DEFAULT_SYMBOL_BATCH)
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--limit-symbols", type=int, default=None)
    parser.add_argument(
        "--daily-only",
        action="store_true",
        help="Download only official daily bars for the daily-pair-v1 backtest.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    target_from = date.fromisoformat(args.date_from)
    target_to = date.fromisoformat(args.date_to)
    if target_from > target_to or target_to >= date.today():
        raise OfflineBacktestError("回测日期必须是有效的已结束历史区间。")
    if args.symbols_per_batch < 1 or args.symbols_per_batch > 50:
        raise OfflineBacktestError("symbols-per-batch 必须在1到50之间。")
    if args.workers < 1 or args.workers > 6:
        raise OfflineBacktestError("workers 必须在1到6之间。")

    output = validate_local_output(Path(args.output))
    output.mkdir(parents=True, exist_ok=True)
    token = load_gm_token(Path(args.config))
    provider = GmOfflineProvider(token)

    if args.command == "probe":
        run_probe(provider, output, target_from, target_to, args.daily_only)
        return 0

    if args.command in ("prepare", "run"):
        prepare_snapshot(provider, output, target_from, target_to, args.daily_only)
    if args.command in ("download", "run"):
        download_snapshot(
            token,
            output,
            args.symbols_per_batch,
            args.workers,
            args.limit_symbols,
            args.daily_only,
        )
    return 0


def validate_local_output(value: Path) -> Path:
    resolved = value.expanduser().resolve()
    if str(resolved).startswith("\\\\"):
        raise OfflineBacktestError("离线回测禁止使用UNC/网络共享输出目录。")
    if resolved.anchor == str(resolved):
        raise OfflineBacktestError("离线回测禁止把磁盘根目录作为输出目录。")
    lowered = str(resolved).lower()
    if any(marker in lowered for marker in ("mysql", "redis", "webapi.env")):
        raise OfflineBacktestError("输出路径包含正式基础设施标识，拒绝运行。")
    return resolved


def load_gm_token(config_path: Path) -> str:
    env_token = os.environ.get("ASTOCK_TOKEN", "").strip()
    if env_token:
        return env_token
    try:
        payload = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise OfflineBacktestError(f"无法读取私有采集配置: {type(error).__name__}") from error
    token = str(payload.get("gmToken", "")).strip()
    if not token or token.startswith("set-"):
        raise OfflineBacktestError("私有配置缺少有效 gmToken。")
    return token


class GmOfflineProvider:
    def __init__(self, token: str) -> None:
        self._token = token
        self._gm: Any | None = None

    def sdk(self) -> Any:
        if self._gm is not None:
            return self._gm
        try:
            import gm.api as gm  # type: ignore[import-not-found]
        except ImportError as error:
            raise OfflineBacktestError("当前Python环境没有安装东方财富GM SDK。") from error
        gm.set_token(self._token)
        required = ("history", "get_trading_dates", "get_instrumentinfos", "get_history_instruments")
        missing = [name for name in required if not callable(getattr(gm, name, None))]
        if missing:
            raise OfflineBacktestError(f"GM SDK缺少接口: {','.join(missing)}")
        self._gm = gm
        return gm

    def trading_dates(self, start: date, end: date) -> list[date]:
        gm = self.sdk()
        calendars: dict[str, list[date]] = {}
        for exchange in ("SHSE", "SZSE"):
            values = retry_call(
                f"get_trading_dates({exchange})",
                lambda exchange=exchange: gm.get_trading_dates(
                    exchange=exchange,
                    start_date=start.isoformat(),
                    end_date=end.isoformat(),
                ),
            )
            calendars[exchange] = sorted({to_date(value) for value in values or []})
        if calendars["SHSE"] != calendars["SZSE"]:
            raise OfflineBacktestError("GM沪深交易日历不一致。")
        return calendars["SHSE"]

    def instrument_metadata(self) -> dict[str, dict[str, Any]]:
        gm = self.sdk()
        sec_type_stock = getattr(gm, "SEC_TYPE_STOCK", 1)
        result = retry_call(
            "get_instrumentinfos",
            lambda: gm.get_instrumentinfos(
                exchanges=["SHSE", "SZSE"],
                sec_types=[sec_type_stock],
                fields="symbol,sec_name,listed_date,delisted_date",
                df=True,
            ),
        )
        metadata: dict[str, dict[str, Any]] = {}
        for row in rows(result):
            symbol = normalize_symbol(row.get("symbol"))
            if not STRICT_A_SHARE.fullmatch(symbol):
                continue
            if symbol in metadata:
                raise OfflineBacktestError(f"历史证券元数据重复: {symbol}")
            name = str(row.get("sec_name") or row.get("name") or "").strip()
            if not name:
                raise OfflineBacktestError(f"历史证券缺少名称: {symbol}")
            metadata[symbol] = {
                "symbol": symbol,
                "name": name,
                "listedDate": optional_date(row.get("listed_date")),
                "delistedDate": optional_date(row.get("delisted_date")),
            }
        return metadata

    def historical_status(
        self,
        trading_date: date,
        candidates: Sequence[str],
    ) -> dict[str, tuple[int, bool]]:
        gm = self.sdk()
        date_text = trading_date.isoformat()
        output: dict[str, tuple[int, bool]] = {}
        for symbol_batch in chunks(list(candidates), HISTORY_INSTRUMENT_BATCH):
            result = retry_call(
                f"get_history_instruments({date_text})",
                lambda symbol_batch=symbol_batch: gm.get_history_instruments(
                    symbols=symbol_batch,
                    start_date=date_text,
                    end_date=date_text,
                    fields="symbol,sec_level,is_suspended,created_at",
                    df=True,
                ),
            )
            requested = set(symbol_batch)
            for row in rows(result):
                symbol = normalize_symbol(row.get("symbol"))
                if symbol not in requested:
                    raise OfflineBacktestError(f"历史状态返回批次外证券: {symbol}")
                if symbol in output:
                    raise OfflineBacktestError(f"历史状态证券重复: {symbol}/{date_text}")
                actual_date = optional_date(row.get("created_at"))
                if actual_date is not None and actual_date != trading_date:
                    raise OfflineBacktestError(
                        f"历史状态日期错位: {symbol}/{actual_date}/{date_text}"
                    )
                try:
                    sec_level = int(row["sec_level"])
                except (KeyError, TypeError, ValueError) as error:
                    raise OfflineBacktestError(f"历史状态缺少sec_level: {symbol}") from error
                output[symbol] = (sec_level, strict_bool(row.get("is_suspended")))
        missing = sorted(set(candidates) - set(output))
        if missing:
            raise OfflineBacktestError(
                f"{date_text}历史状态缺少{len(missing)}只证券，示例:{missing[:10]}"
            )
        return output

    def history(
        self,
        symbols: Sequence[str],
        frequency: str,
        start: datetime,
        end: datetime,
    ) -> list[dict[str, Any]]:
        gm = self.sdk()
        if frequency not in FREQUENCIES:
            raise OfflineBacktestError(f"不支持的频率: {frequency}")
        result = retry_call(
            f"history({frequency},{start.date()},{end.date()})",
            lambda: gm.history(
                symbol=",".join(symbols),
                frequency=FREQUENCIES[frequency],
                start_time=start.strftime("%Y-%m-%d %H:%M:%S"),
                end_time=end.strftime("%Y-%m-%d %H:%M:%S"),
                fields=FIELDS,
                adjust=getattr(gm, "ADJUST_NONE", None),
                df=True,
            ),
        )
        normalized = [normalize_bar(row, frequency) for row in rows(result)]
        requested = set(symbols)
        unexpected = sorted({bar["symbol"] for bar in normalized} - requested)
        if unexpected:
            raise OfflineBacktestError(f"history返回批次外证券: {unexpected[:10]}")
        return normalized


def run_probe(
    provider: GmOfflineProvider,
    output: Path,
    target_from: date,
    target_to: date,
    daily_only: bool,
) -> None:
    calendar = build_calendar(provider, target_from, target_to)
    metadata = provider.instrument_metadata()
    first_date = calendar.target_dates[0]
    last_date = calendar.target_dates[-1]
    candidates = active_candidates(metadata, first_date)
    sample = [symbol for symbol in ("SHSE.600000", "SZSE.000001", "SHSE.600519", "SZSE.000858", "SHSE.601318") if symbol in candidates]
    if len(sample) < 5:
        sample = candidates[:5]
    status_first = provider.historical_status(first_date, sample)
    status_last = provider.historical_status(last_date, sample)
    bars: dict[str, Any] = {}
    failures: dict[str, str] = {}
    requested_frequencies = ("1d",) if daily_only else tuple(FREQUENCIES)
    for frequency in requested_frequencies:
        try:
            values = provider.history(
                sample,
                frequency,
                datetime.combine(first_date, clock_time(9, 30)),
                datetime.combine(first_date, clock_time(15, 0)),
            )
            validate_probe_bars(values, sample, frequency, first_date)
            bars[frequency] = {
                "status": "available",
                "rows": len(values),
                "symbols": len({value["symbol"] for value in values}),
                "firstEob": min(value["eob"] for value in values),
                "lastEob": max(value["eob"] for value in values),
            }
        except OfflineBacktestError as error:
            failures[frequency] = sanitize(error)
            bars[frequency] = {"status": "unavailable", "error": sanitize(error)}
    payload = {
        "snapshotVersion": SNAPSHOT_VERSION,
        "checkedAt": datetime.now().isoformat(timespec="seconds"),
        "targetFrom": target_from.isoformat(),
        "targetTo": target_to.isoformat(),
        "calculationMode": "daily-pair-v1" if daily_only else ALGORITHM_VERSION,
        "firstTradingDate": first_date.isoformat(),
        "lastTradingDate": last_date.isoformat(),
        "sampleSymbols": sample,
        "firstStatusRows": len(status_first),
        "lastStatusRows": len(status_last),
        "bars": bars,
        "barPermissionFailures": failures,
        "isolation": {
            "webApiClient": False,
            "mysqlClient": False,
            "redisClient": False,
            "migration": False,
        },
    }
    write_json_atomic(output / "probe.json", payload)
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    if failures:
        raise OfflineBacktestError(
            "GM历史K线权限探针失败: " + ",".join(sorted(failures))
        )


def prepare_snapshot(
    provider: GmOfflineProvider,
    output: Path,
    target_from: date,
    target_to: date,
    daily_only: bool,
) -> None:
    manifest_path = output / "snapshot-manifest.json"
    if manifest_path.exists():
        existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        if existing.get("stage") in ("prepared", "downloaded"):
            assert_manifest_scope(existing, target_from, target_to)
            expected_mode = "daily-pair-v1" if daily_only else ALGORITHM_VERSION
            if existing.get("calculationMode") != expected_mode:
                raise OfflineBacktestError("已有快照计算模式与本次请求不一致，拒绝混用。")
            print("prepare already complete; using existing local snapshot manifest")
            return

    calendar = build_calendar(provider, target_from, target_to)
    metadata = provider.instrument_metadata()
    universe_dir = output / "universe"
    universe_dir.mkdir(parents=True, exist_ok=True)
    eligibility: dict[str, list[tuple[date, str]]] = {}
    universe_hashes: dict[str, str] = {}

    for index, trading_date in enumerate(calendar.target_dates, start=1):
        candidates = active_candidates(metadata, trading_date)
        if not MIN_HISTORICAL_SYMBOLS <= len(candidates) <= MAX_HISTORICAL_SYMBOLS:
            raise OfflineBacktestError(
                f"{trading_date}历史候选数量异常: {len(candidates)}"
            )
        first = provider.historical_status(trading_date, candidates)
        second = provider.historical_status(trading_date, candidates)
        if first != second:
            raise OfflineBacktestError(f"{trading_date}连续两轮历史状态不一致。")
        records = []
        eligible = 0
        for symbol in candidates:
            sec_level, suspended = second[symbol]
            item = metadata[symbol]
            is_eligible = sec_level == 1 and not suspended
            if is_eligible:
                eligible += 1
                eligibility.setdefault(symbol, []).append((trading_date, item["name"]))
            records.append(
                {
                    "symbol": symbol,
                    "name": item["name"],
                    "secLevel": sec_level,
                    "isSuspended": suspended,
                    "isEligible": is_eligible,
                }
            )
        if eligible < MIN_HISTORICAL_SYMBOLS:
            raise OfflineBacktestError(
                f"{trading_date}历史可用证券数量异常: {eligible}"
            )
        target = universe_dir / f"{trading_date.isoformat()}.json.gz"
        write_gzip_json_atomic(target, records)
        universe_hashes[trading_date.isoformat()] = sha256_file(target)
        print(
            f"universe {index}/{len(calendar.target_dates)} date={trading_date} "
            f"candidates={len(candidates)} eligible={eligible}",
            flush=True,
        )

    eligibility_path = output / "eligibility.tsv.gz"
    write_eligibility(eligibility_path, eligibility)
    symbols = sorted(eligibility)
    calendar_payload = {
        "targetDates": [value.isoformat() for value in calendar.target_dates],
        "dailyDates": [value.isoformat() for value in calendar.daily_dates],
        "dailyFrom": calendar.daily_from.isoformat(),
        "dailyTo": calendar.daily_to.isoformat(),
    }
    write_json_atomic(output / "calendar.json", calendar_payload)
    manifest = {
        "snapshotVersion": SNAPSHOT_VERSION,
        "stage": "prepared",
        "algorithmVersion": ALGORITHM_VERSION,
        "waveAlgorithmVersion": WAVE_ALGORITHM_VERSION,
        "calculationMode": "daily-pair-v1" if daily_only else ALGORITHM_VERSION,
        "dailyOnly": daily_only,
        "targetFrom": target_from.isoformat(),
        "targetTo": target_to.isoformat(),
        "targetTradingDays": len(calendar.target_dates),
        "dailyFrom": calendar.daily_from.isoformat(),
        "dailyTo": calendar.daily_to.isoformat(),
        "dailyTradingDays": len(calendar.daily_dates),
        "symbols": symbols,
        "eligibilitySha256": sha256_file(eligibility_path),
        "universeHashes": universe_hashes,
        "completedBatches": {},
        "isolation": {
            "webApiClient": False,
            "mysqlClient": False,
            "redisClient": False,
            "migration": False,
        },
    }
    write_json_atomic(manifest_path, manifest)


def download_snapshot(
    token: str,
    output: Path,
    symbols_per_batch: int,
    workers: int,
    limit_symbols: int | None,
    daily_only: bool,
) -> None:
    manifest_path = output / "snapshot-manifest.json"
    if not manifest_path.exists():
        raise OfflineBacktestError("缺少prepare阶段的snapshot-manifest.json。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("stage") not in ("prepared", "downloaded"):
        raise OfflineBacktestError("本地快照未完成prepare。")
    if bool(manifest.get("dailyOnly")) != daily_only:
        raise OfflineBacktestError("download的daily-only参数与prepare快照不一致。")
    symbols = list(manifest["symbols"])
    if limit_symbols is not None:
        if limit_symbols < 1:
            raise OfflineBacktestError("limit-symbols必须大于0。")
        symbols = symbols[:limit_symbols]
    eligibility = read_eligibility(output / "eligibility.tsv.gz")
    batches = list(chunks(symbols, symbols_per_batch))
    batch_dir = output / "batches"
    batch_dir.mkdir(parents=True, exist_ok=True)
    calendar = json.loads((output / "calendar.json").read_text(encoding="utf-8"))
    completed: dict[str, Any] = dict(manifest.get("completedBatches") or {})
    pending: list[dict[str, Any]] = []
    for index, symbol_batch in enumerate(batches):
        batch_id = f"batch-{index:04d}"
        target = batch_dir / f"{batch_id}.jsonl.gz"
        existing = completed.get(batch_id)
        if target.exists() and existing and existing.get("sha256") == sha256_file(target):
            continue
        pending.append(
            {
                "batchId": batch_id,
                "symbols": symbol_batch,
                "eligibility": {
                    symbol: [value.isoformat() for value in eligibility.get(symbol, set())]
                    for symbol in symbol_batch
                },
                "targetFrom": manifest["targetFrom"],
                "targetTo": manifest["targetTo"],
                "dailyFrom": calendar["dailyFrom"],
                "dailyTo": calendar["dailyTo"],
                "dailyOnly": daily_only,
                "output": str(target),
            }
        )
    if not pending:
        print("all requested local batches already downloaded")
        return

    print(
        f"download batches pending={len(pending)} total={len(batches)} "
        f"symbols={len(symbols)} workers={workers}",
        flush=True,
    )
    if workers == 1:
        for plan in pending:
            result = download_one_batch(token, plan)
            completed[result["batchId"]] = result
            persist_download_progress(manifest_path, manifest, completed, len(batches), limit_symbols)
            print_batch_result(result, len(completed), len(batches))
    else:
        with ProcessPoolExecutor(max_workers=workers) as executor:
            futures = {executor.submit(download_one_batch, token, plan): plan for plan in pending}
            for future in as_completed(futures):
                result = future.result()
                completed[result["batchId"]] = result
                persist_download_progress(manifest_path, manifest, completed, len(batches), limit_symbols)
                print_batch_result(result, len(completed), len(batches))


def download_one_batch(token: str, plan: dict[str, Any]) -> dict[str, Any]:
    provider = GmOfflineProvider(token)
    symbols = list(plan["symbols"])
    target_from = date.fromisoformat(plan["targetFrom"])
    target_to = date.fromisoformat(plan["targetTo"])
    daily_from = date.fromisoformat(plan["dailyFrom"])
    daily_to = date.fromisoformat(plan["dailyTo"])
    eligibility = {
        symbol: {date.fromisoformat(value) for value in values}
        for symbol, values in plan["eligibility"].items()
    }
    all_bars: dict[tuple[str, str, str], dict[str, Any]] = {}
    sparse: list[dict[str, Any]] = []

    intraday_frequencies: tuple[str, ...] = () if plan.get("dailyOnly") else ("5m", "30m", "60m")
    for frequency in intraday_frequencies:
        for month_from, month_to in month_ranges(target_from, target_to):
            values = provider.history(
                symbols,
                frequency,
                datetime.combine(month_from, clock_time(9, 30)),
                datetime.combine(month_to, clock_time(15, 0)),
            )
            for bar in values:
                trading_date = date.fromisoformat(bar["tradingDate"])
                if trading_date not in eligibility.get(bar["symbol"], set()):
                    continue
                validate_session_eob(bar, trading_date)
                add_bar(all_bars, bar)

        for symbol in symbols:
            for trading_date in sorted(eligibility.get(symbol, set())):
                expected = expected_eobs(frequency, trading_date)
                actual = {
                    datetime.fromisoformat(key[2])
                    for key in all_bars
                    if key[0] == symbol and key[1] == frequency
                    and datetime.fromisoformat(key[2]).date() == trading_date
                }
                if actual == expected:
                    continue
                first_map = day_map(all_bars, symbol, frequency, trading_date)
                signatures = [bar_map_signature(first_map)]
                probe_maps: list[dict[datetime, dict[str, Any]]] = []
                for _ in range(2):
                    probe_rows = provider.history(
                        [symbol],
                        frequency,
                        datetime.combine(trading_date, clock_time(9, 30)),
                        datetime.combine(trading_date, clock_time(15, 0)),
                    )
                    probe_map: dict[datetime, dict[str, Any]] = {}
                    for bar in probe_rows:
                        validate_session_eob(bar, trading_date)
                        eob = datetime.fromisoformat(bar["eob"])
                        if eob in probe_map:
                            raise OfflineBacktestError(
                                f"稀疏探针重复EOB: {symbol}/{frequency}/{eob}"
                            )
                        probe_map[eob] = bar
                    signatures.append(bar_map_signature(probe_map))
                    probe_maps.append(probe_map)
                if len(set(signatures)) != 1:
                    raise OfflineBacktestError(
                        f"三次稀疏证明不一致: {symbol}/{frequency}/{trading_date}"
                    )
                missing = sorted(expected - set(first_map))
                extra = sorted(set(first_map) - expected)
                if extra:
                    raise OfflineBacktestError(
                        f"官方K线包含非法EOB: {symbol}/{frequency}/{extra[:5]}"
                    )
                sparse.append(
                    {
                        "symbol": symbol,
                        "frequency": frequency,
                        "tradingDate": trading_date.isoformat(),
                        "missingEobs": [value.isoformat(timespec="seconds") for value in missing],
                        "confirmations": 3,
                    }
                )

    for month_from, month_to in month_ranges(daily_from, daily_to):
        values = provider.history(
            symbols,
            "1d",
            datetime.combine(month_from, clock_time(0, 0)),
            datetime.combine(month_to, clock_time(23, 59, 59)),
        )
        for bar in values:
            add_bar(all_bars, bar)

    ordered = sorted(
        all_bars.values(),
        key=lambda value: (value["symbol"], frequency_rank(value["frequency"]), value["eob"]),
    )
    target = Path(plan["output"])
    write_gzip_json_lines_atomic(target, ordered)
    sparse_target = target.with_suffix(".sparse.json")
    write_json_atomic(sparse_target, sparse)
    counts = {
        frequency: sum(1 for bar in ordered if bar["frequency"] == frequency)
        for frequency in FREQUENCIES
    }
    return {
        "batchId": plan["batchId"],
        "symbols": len(symbols),
        "bars": len(ordered),
        "counts": counts,
        "sparse": len(sparse),
        "sha256": sha256_file(target),
        "sparseSha256": sha256_file(sparse_target),
    }


def build_calendar(provider: GmOfflineProvider, target_from: date, target_to: date) -> CalendarRange:
    search_from = target_from - timedelta(days=400)
    search_to = target_to + timedelta(days=90)
    dates = provider.trading_dates(search_from, search_to)
    target_dates = [value for value in dates if target_from <= value <= target_to]
    before = [value for value in dates if value < target_dates[0]]
    after = [value for value in dates if value > target_dates[-1]]
    if len(before) < 120 or len(after) < 20:
        raise OfflineBacktestError("GM交易日历不足120日前置或20日后置观察窗口。")
    daily_dates = before[-120:] + target_dates + after[:20]
    return CalendarRange(
        tuple(target_dates),
        tuple(daily_dates),
        daily_dates[0],
        daily_dates[-1],
    )


def active_candidates(metadata: dict[str, dict[str, Any]], trading_date: date) -> list[str]:
    return sorted(
        symbol
        for symbol, item in metadata.items()
        if (item["listedDate"] is None or item["listedDate"] <= trading_date)
        and (item["delistedDate"] is None or item["delistedDate"] >= trading_date)
    )


def validate_probe_bars(
    values: Sequence[dict[str, Any]], symbols: Sequence[str], frequency: str, trading_date: date
) -> None:
    if not values:
        raise OfflineBacktestError(f"探针{frequency}没有返回K线。")
    for value in values:
        if value["symbol"] not in symbols:
            raise OfflineBacktestError("探针返回批次外证券。")
        validate_session_eob(value, trading_date)


def validate_session_eob(bar: dict[str, Any], trading_date: date) -> None:
    eob = datetime.fromisoformat(bar["eob"])
    if eob.date() != trading_date or eob not in expected_eobs(bar["frequency"], trading_date):
        raise OfflineBacktestError(
            f"非交易时段EOB: {bar['symbol']}/{bar['frequency']}/{bar['eob']}"
        )


def expected_eobs(frequency: str, trading_date: date) -> set[datetime]:
    if frequency == "5m":
        values: list[datetime] = []
        current = datetime.combine(trading_date, clock_time(9, 35))
        while current.time() <= clock_time(11, 30):
            values.append(current)
            current += timedelta(minutes=5)
        current = datetime.combine(trading_date, clock_time(13, 5))
        while current.time() <= clock_time(15, 0):
            values.append(current)
            current += timedelta(minutes=5)
        return set(values)
    closes = {
        "30m": ((10, 0), (10, 30), (11, 0), (11, 30), (13, 30), (14, 0), (14, 30), (15, 0)),
        "60m": ((10, 30), (11, 30), (14, 0), (15, 0)),
        "1d": ((15, 0),),
    }
    if frequency not in closes:
        raise OfflineBacktestError(f"不支持的周期: {frequency}")
    return {
        datetime.combine(trading_date, clock_time(hour, minute))
        for hour, minute in closes[frequency]
    }


def normalize_bar(row: dict[str, Any], frequency: str) -> dict[str, Any]:
    def get(*names: str) -> Any:
        for name in names:
            if name in row and row[name] is not None:
                return row[name]
        return None

    symbol = normalize_symbol(get("symbol"))
    if not STRICT_A_SHARE.fullmatch(symbol):
        raise OfflineBacktestError(f"K线证券代码不合法: {symbol}")
    bob = to_datetime(get("bob", "begin_time"))
    eob = to_datetime(get("eob", "end_time"))
    if frequency == "1d":
        if bob.date() != eob.date() or bob.time() != clock_time() or eob.time() != clock_time():
            raise OfflineBacktestError(f"日K时间语义异常: {symbol}/{bob}/{eob}")
        bob = datetime.combine(bob.date(), clock_time(9, 30))
        eob = datetime.combine(eob.date(), clock_time(15, 0))
    source: dict[str, Any] = {
        "symbol": symbol,
        "frequency": frequency,
        "tradingDate": eob.date().isoformat(),
        "bob": bob.isoformat(timespec="seconds"),
        "eob": eob.isoformat(timespec="seconds"),
        "openPrice": finite_number(get("open")),
        "highPrice": finite_number(get("high")),
        "lowPrice": finite_number(get("low")),
        "closePrice": finite_number(get("close")),
        "preClose": optional_number(get("pre_close", "preclose")),
        "volume": integer(get("volume")),
        "amount": finite_number(get("amount")),
    }
    if source["highPrice"] < max(source["openPrice"], source["closePrice"]):
        raise OfflineBacktestError(f"K线最高价质量失败: {symbol}/{frequency}/{eob}")
    if source["lowPrice"] > min(source["openPrice"], source["closePrice"]):
        raise OfflineBacktestError(f"K线最低价质量失败: {symbol}/{frequency}/{eob}")
    source["sourceRowHash"] = hashlib.sha256(
        json.dumps(source, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    return source


def add_bar(target: dict[tuple[str, str, str], dict[str, Any]], bar: dict[str, Any]) -> None:
    key = (bar["symbol"], bar["frequency"], bar["eob"])
    existing = target.get(key)
    if existing is not None and existing["sourceRowHash"] != bar["sourceRowHash"]:
        raise OfflineBacktestError(f"同EOB出现不同官方内容: {key}")
    target[key] = bar


def day_map(
    bars: dict[tuple[str, str, str], dict[str, Any]],
    symbol: str,
    frequency: str,
    trading_date: date,
) -> dict[datetime, dict[str, Any]]:
    return {
        datetime.fromisoformat(key[2]): value
        for key, value in bars.items()
        if key[0] == symbol and key[1] == frequency
        and datetime.fromisoformat(key[2]).date() == trading_date
    }


def bar_map_signature(values: dict[datetime, dict[str, Any]]) -> str:
    material = [(key.isoformat(), value["sourceRowHash"]) for key, value in sorted(values.items())]
    return hashlib.sha256(json.dumps(material, separators=(",", ":")).encode()).hexdigest()


def month_ranges(start: date, end: date) -> Iterable[tuple[date, date]]:
    current = start
    while current <= end:
        next_month = (current.replace(day=28) + timedelta(days=4)).replace(day=1)
        chunk_end = min(end, next_month - timedelta(days=1))
        yield current, chunk_end
        current = chunk_end + timedelta(days=1)


def rows(result: Any) -> Iterable[dict[str, Any]]:
    if result is None:
        return []
    if hasattr(result, "to_dict"):
        return result.to_dict("records")
    if isinstance(result, dict):
        return [result]
    return result


def retry_call(label: str, action: Any) -> Any:
    for attempt in range(len(RETRY_DELAYS) + 1):
        try:
            return action()
        except Exception as error:
            detail = str(error)
            status = str(getattr(error, "status", ""))
            deterministic = (
                status in ("1026", "2002")
                or '"status": 2002' in detail
                or "ERR_NO_DATA_PERMISSION" in detail
            )
            if deterministic or attempt >= len(RETRY_DELAYS):
                raise OfflineBacktestError(
                    f"GM {label}失败: {type(error).__name__}:{sanitize(error)}"
                ) from error
            time.sleep(RETRY_DELAYS[attempt])
    raise AssertionError("unreachable retry state")


def sanitize(error: BaseException) -> str:
    text = str(error).replace("\r", " ").replace("\n", " ")
    return re.sub(r"(?i)(token|password|api[_-]?key)\s*[:=]\s*[^\s,;]+", r"\1=<redacted>", text)[:500]


def normalize_symbol(value: Any) -> str:
    return str(value or "").strip().upper()


def to_date(value: Any) -> date:
    converted = optional_date(value)
    if converted is None:
        raise OfflineBacktestError(f"无法解析交易日: {value}")
    return converted


def optional_date(value: Any) -> date | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    converter = getattr(value, "to_pydatetime", None)
    if callable(converter):
        converted = converter()
        return converted.date() if isinstance(converted, datetime) else converted
    text = str(value).strip()
    if not text or text.lower() in ("nat", "nan", "none", "null"):
        return None
    return date.fromisoformat(text[:10])


def to_datetime(value: Any) -> datetime:
    if isinstance(value, datetime):
        return value.replace(tzinfo=None)
    converter = getattr(value, "to_pydatetime", None)
    if callable(converter):
        return converter().replace(tzinfo=None)
    return datetime.fromisoformat(str(value).strip().replace("Z", "+00:00")).replace(tzinfo=None)


def strict_bool(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in ("1", "1.0", "true"):
        return True
    if text in ("0", "0.0", "false"):
        return False
    raise OfflineBacktestError(f"无效布尔值: {value}")


def finite_number(value: Any) -> float:
    try:
        result = float(value)
    except (TypeError, ValueError) as error:
        raise OfflineBacktestError(f"无法转换数值: {value}") from error
    if result != result or result in (float("inf"), float("-inf")):
        raise OfflineBacktestError(f"非有限数值: {value}")
    return result


def optional_number(value: Any) -> float | None:
    return None if value is None else finite_number(value)


def integer(value: Any) -> int:
    try:
        return int(float(value))
    except (TypeError, ValueError) as error:
        raise OfflineBacktestError(f"无法转换整数: {value}") from error


def chunks(values: list[str], size: int) -> Iterable[list[str]]:
    for index in range(0, len(values), size):
        yield values[index:index + size]


def frequency_rank(value: str) -> int:
    return {"5m": 1, "30m": 2, "60m": 3, "1d": 4}[value]


def write_eligibility(path: Path, values: dict[str, list[tuple[date, str]]]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as writer:
                for symbol in sorted(values):
                    for trading_date, name in values[symbol]:
                        safe_name = name.replace("\t", " ").replace("\r", " ").replace("\n", " ")
                        writer.write(f"{symbol}\t{trading_date.isoformat()}\t{safe_name}\n")
    os.replace(temporary, path)


def read_eligibility(path: Path) -> dict[str, set[date]]:
    output: dict[str, set[date]] = {}
    with gzip.open(path, "rt", encoding="utf-8") as reader:
        for line in reader:
            symbol, date_text, _ = line.rstrip("\n").split("\t", 2)
            output.setdefault(symbol, set()).add(date.fromisoformat(date_text))
    return output


def write_gzip_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as compressed:
            compressed.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    os.replace(temporary, path)


def write_gzip_json_lines_atomic(path: Path, values: Iterable[dict[str, Any]]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as writer:
                for value in values:
                    writer.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")))
                    writer.write("\n")
    os.replace(temporary, path)


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as reader:
        while chunk := reader.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def assert_manifest_scope(manifest: dict[str, Any], target_from: date, target_to: date) -> None:
    if manifest.get("targetFrom") != target_from.isoformat() or manifest.get("targetTo") != target_to.isoformat():
        raise OfflineBacktestError("已有快照日期范围与本次请求不一致，拒绝混用。")


def persist_download_progress(
    path: Path,
    manifest: dict[str, Any],
    completed: dict[str, Any],
    total_batches: int,
    limit_symbols: int | None,
) -> None:
    updated = dict(manifest)
    updated["completedBatches"] = dict(sorted(completed.items()))
    updated["requestedBatchCount"] = total_batches
    updated["limitSymbols"] = limit_symbols
    updated["stage"] = "downloaded" if len(completed) >= total_batches else "prepared"
    updated["updatedAt"] = datetime.now().isoformat(timespec="seconds")
    write_json_atomic(path, updated)
    manifest.clear()
    manifest.update(updated)


def print_batch_result(result: dict[str, Any], completed: int, total: int) -> None:
    print(
        f"download {completed}/{total} {result['batchId']} symbols={result['symbols']} "
        f"bars={result['bars']} sparse={result['sparse']} sha256={result['sha256'][:12]}",
        flush=True,
    )


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except OfflineBacktestError as error:
        print(f"OFFLINE_BACKTEST_ERROR: {sanitize(error)}", file=sys.stderr)
        raise SystemExit(1)
