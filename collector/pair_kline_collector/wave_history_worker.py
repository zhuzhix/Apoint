"""Dedicated one-process collector for on-demand wave-bottom daily history.

This process only fetches official daily bars and uploads them. Wave scoring and
all persistence are owned by WebAPI.
"""

from __future__ import annotations

import argparse
import logging
import time
from collections import defaultdict
from datetime import date, datetime, time as clock_time
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any

import main


LOGGER = logging.getLogger("wave-bottom-history-collector")
MAXIMUM_SYMBOLS_PER_CLAIM = 200


def configure_logging(state_directory: Path) -> None:
    state_directory.mkdir(parents=True, exist_ok=True)
    formatter = logging.Formatter(
        "%(asctime)s %(levelname)s pid=%(process)d %(message)s"
    )
    stream = logging.StreamHandler()
    stream.setFormatter(formatter)
    file_handler = RotatingFileHandler(
        state_directory / "wave-history-collector.log",
        maxBytes=10 * 1024 * 1024,
        backupCount=5,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    LOGGER.handlers.clear()
    LOGGER.setLevel(logging.INFO)
    LOGGER.addHandler(stream)
    LOGGER.addHandler(file_handler)
    # Provider helpers log through main.LOGGER. Route those messages to the
    # dedicated file instead of concurrently rotating collector.log.
    main.LOGGER.handlers.clear()
    main.LOGGER.setLevel(logging.INFO)
    main.LOGGER.addHandler(file_handler)


def _wave_bar(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "symbol": row["symbol"],
        "tradingDate": datetime.fromisoformat(row["eob"]).date().isoformat(),
        "openPrice": row["openPrice"],
        "highPrice": row["highPrice"],
        "lowPrice": row["lowPrice"],
        "closePrice": row["closePrice"],
        "preClose": row.get("preClose"),
        "volume": row["volume"],
        "amount": row["amount"],
        "sourceRowHash": row["sourceRowHash"],
    }


def collect_claim(
    settings: main.Settings,
    client: main.ApiClient,
    provider: main.GmHistoryProvider,
    claim: dict[str, Any],
) -> dict[str, Any]:
    lease_token = str(claim.get("leaseToken") or "").strip()
    jobs = list(claim.get("jobs") or [])
    if not lease_token or not jobs:
        return {"status": "empty"}
    distinct_symbols = {str(job["symbol"]).upper() for job in jobs}
    if len(distinct_symbols) > MAXIMUM_SYMBOLS_PER_CLAIM:
        raise main.CollectorFatalError(
            f"WebAPI 下发 {len(distinct_symbols)} 只波段股票，超过进程上限 200。"
        )

    groups: dict[tuple[date, int], set[str]] = defaultdict(set)
    for job in jobs:
        groups[(date.fromisoformat(job["dataEndDate"]), int(job["requiredDailyBars"]))].add(
            str(job["symbol"]).upper()
        )

    collected: dict[tuple[str, str], dict[str, Any]] = {}
    failures: dict[str, str] = {}
    try:
        for (data_end_date, required_bars), symbols in sorted(groups.items()):
            trading_dates = provider.completed_trading_dates(data_end_date, required_bars)
            allowed_dates = set(trading_dates)
            ordered_symbols = sorted(symbols)
            for offset in range(0, len(ordered_symbols), settings.symbols_per_sdk_request):
                requested = ordered_symbols[offset : offset + settings.symbols_per_sdk_request]
                try:
                    rows = provider.fetch(
                        requested,
                        "1d",
                        datetime.combine(trading_dates[0], clock_time.min),
                        datetime.combine(trading_dates[-1], clock_time.max).replace(microsecond=0),
                    )
                    unexpected = {
                        str(row.get("symbol") or "").upper() for row in rows
                    } - set(requested)
                    if unexpected:
                        raise main.CollectorError(
                            f"gm 波段日K返回批次外证券: {sorted(unexpected)}"
                        )
                    for row in rows:
                        symbol = str(row["symbol"]).upper()
                        trading_date = datetime.fromisoformat(row["eob"]).date()
                        if trading_date not in allowed_dates:
                            raise main.CollectorError(
                                f"{symbol} 返回计划外波段日K日期 {trading_date.isoformat()}"
                            )
                        payload = _wave_bar(row)
                        key = (symbol, payload["tradingDate"])
                        existing = collected.get(key)
                        if existing and existing["sourceRowHash"] != payload["sourceRowHash"]:
                            raise main.CollectorError(
                                f"{symbol}/{payload['tradingDate']} 波段日K哈希冲突"
                            )
                        collected[key] = payload
                except main.ProviderUnavailableError:
                    raise
                except main.CollectorError as error:
                    reason = str(main.sanitize_error(str(error)))
                    for symbol in requested:
                        failures[symbol] = reason

        bars = sorted(collected.values(), key=lambda item: (item["symbol"], item["tradingDate"]))
        maximum_batch = max(1, min(int(claim.get("maximumBarsPerBatch") or 2_000), 2_000))
        for offset in range(0, len(bars), maximum_batch):
            client.push_wave_bottom_batch(lease_token, bars[offset : offset + maximum_batch])
        result = client.complete_wave_bottom_jobs(
            lease_token,
            [{"symbol": symbol, "error": error} for symbol, error in sorted(failures.items())],
        )
        LOGGER.info(
            "wave claim completed: jobs=%s symbols=%s bars=%s failures=%s result=%s",
            len(jobs), len(distinct_symbols), len(bars), len(failures), result,
        )
        return result
    except Exception as error:
        provider_unavailable = isinstance(error, main.ProviderUnavailableError)
        client.fail_wave_bottom_lease(
            lease_token, str(main.sanitize_error(str(error))), provider_unavailable
        )
        raise
    finally:
        collected.clear()
        failures.clear()


def run(settings: main.Settings, once: bool = False) -> int:
    client = main.ApiClient(settings)
    provider = main.GmHistoryProvider(settings.gm_token)
    while True:
        try:
            claim = client.claim_wave_bottom_jobs(MAXIMUM_SYMBOLS_PER_CLAIM)
            if not claim.get("leaseToken") or not claim.get("jobs"):
                if once:
                    return 0
                time.sleep(max(5, settings.poll_seconds))
                continue
            collect_claim(settings, client, provider, claim)
        except main.CollectorFatalError as error:
            LOGGER.critical("wave collector fatal error: %s", main.sanitize_error(str(error)))
            return 1
        except main.CollectorError as error:
            LOGGER.error("wave collection failed: %s", main.sanitize_error(str(error)))
            if once:
                return 1
            time.sleep(max(5, settings.poll_seconds))
        if once:
            return 0


def entrypoint() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.local.json")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    try:
        settings = main.Settings.load(Path(args.config).resolve())
        configure_logging(Path(settings.state_directory))
        if settings.provider != "gm":
            raise main.CollectorError(f"不支持的数据源 provider: {settings.provider}")
        LOGGER.info("wave history collector %s started", main.COLLECTOR_VERSION)
        return run(settings, once=args.once)
    except Exception as error:
        LOGGER.exception("wave history collector stopped: %s", main.sanitize_error(str(error)))
        return 1


if __name__ == "__main__":
    raise SystemExit(entrypoint())
