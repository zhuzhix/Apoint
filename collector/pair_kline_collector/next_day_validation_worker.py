"""One-shot official 5-minute collector for historical next-day validation.

Python owns no business rule and stores no bar. It fetches one WebAPI lease at a
time, proves sparse official windows with three identical reads, uploads bars,
and immediately releases all in-memory data after completion.
"""

from __future__ import annotations

import argparse
import logging
from datetime import date, datetime, time as clock_time, timedelta
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any

import main


LOGGER = logging.getLogger("pair-next-day-validation-collector")
MAXIMUM_SYMBOLS_PER_CLAIM = 200


def configure_logging(state_directory: Path) -> None:
    state_directory.mkdir(parents=True, exist_ok=True)
    formatter = logging.Formatter("%(asctime)s %(levelname)s pid=%(process)d %(message)s")
    stream = logging.StreamHandler()
    stream.setFormatter(formatter)
    file_handler = RotatingFileHandler(
        state_directory / "next-day-validation.log",
        maxBytes=10 * 1024 * 1024,
        backupCount=5,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    LOGGER.handlers.clear()
    LOGGER.setLevel(logging.INFO)
    LOGGER.addHandler(stream)
    LOGGER.addHandler(file_handler)
    main.LOGGER.handlers.clear()
    main.LOGGER.setLevel(logging.INFO)
    main.LOGGER.addHandler(file_handler)


def _bar_payload(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "symbol": str(row["symbol"]).upper(),
        "bob": row["bob"],
        "eob": row["eob"],
        "openPrice": row["openPrice"],
        "highPrice": row["highPrice"],
        "lowPrice": row["lowPrice"],
        "closePrice": row["closePrice"],
        "preClose": row.get("preClose"),
        "volume": row["volume"],
        "amount": row["amount"],
        "sourceRowHash": row["sourceRowHash"],
    }


def _validate_rows(
    rows: list[dict[str, Any]],
    requested: list[str],
    expected_eobs: set[datetime],
) -> dict[str, dict[datetime, dict[str, Any]]]:
    requested_set = set(requested)
    result = {symbol: {} for symbol in requested}
    unexpected = {
        str(row.get("symbol") or "").strip().upper() for row in rows
    } - requested_set
    if unexpected:
        raise main.CollectorError(f"次日验证返回批次外证券: {sorted(unexpected)}")
    for row in rows:
        symbol = str(row.get("symbol") or "").strip().upper()
        if symbol not in requested_set:
            raise main.CollectorError("次日验证K线缺少有效证券代码。")
        if str(row.get("frequency") or "").lower() != "5m":
            raise main.CollectorError(f"{symbol} 次日验证返回了非5m周期。")
        eob = datetime.fromisoformat(str(row.get("eob")))
        if eob not in expected_eobs:
            raise main.CollectorError(f"{symbol} 次日验证返回计划外EOB {eob.isoformat()}。")
        if eob in result[symbol]:
            raise main.CollectorError(f"{symbol} 次日验证返回重复EOB {eob.isoformat()}。")
        result[symbol][eob] = row
    return result


def _signature(rows: dict[datetime, dict[str, Any]]) -> tuple[tuple[str, str], ...]:
    return main.sparse_bar_map_signature(rows)


def collect_claim(
    settings: main.Settings,
    client: main.ApiClient,
    provider: main.GmHistoryProvider,
    claim: dict[str, Any],
) -> dict[str, Any]:
    lease_token = str(claim.get("leaseToken") or "").strip()
    symbols = sorted({str(item).strip().upper() for item in claim.get("symbols") or []})
    validation_date = date.fromisoformat(str(claim["validationTradingDate"]))
    if not lease_token or not symbols:
        return {"status": "empty"}
    if len(symbols) > MAXIMUM_SYMBOLS_PER_CLAIM:
        raise main.CollectorFatalError(
            f"WebAPI 下发 {len(symbols)} 只次日验证股票，超过单次上限200。"
        )
    start = datetime.combine(validation_date, clock_time(9, 30))
    end = datetime.combine(validation_date, clock_time(15, 0))
    expected = main.planned_eobs("5m", start, end)
    collected: dict[tuple[str, datetime], dict[str, Any]] = {}
    proofs: dict[str, dict[str, Any]] = {}
    failures: dict[str, str] = {}
    try:
        # 空响应只有在同日期液态样本可正常返回时，才可能被解释为个股无成交。
        anchors = provider.fetch(["SHSE.600000", "SZSE.000001"], "5m", start, end)
        anchor_maps = _validate_rows(anchors, ["SHSE.600000", "SZSE.000001"], expected)
        if not any(anchor_maps.values()):
            raise main.ProviderFrequencyUnavailableError(
                f"{validation_date} 次日验证5m健康样本为空，拒绝把供应商故障解释为无成交。"
            )

        for offset in range(0, len(symbols), settings.symbols_per_sdk_request):
            group = symbols[offset : offset + settings.symbols_per_sdk_request]
            try:
                first = _validate_rows(provider.fetch(group, "5m", start, end), group, expected)
            except main.ProviderUnavailableError:
                raise
            except main.CollectorError as error:
                for symbol in group:
                    failures[symbol] = str(main.sanitize_error(str(error)))
                continue
            for symbol in group:
                try:
                    first_map = first[symbol]
                    missing = sorted(expected - set(first_map))
                    confirmations = 1
                    if missing:
                        second = _validate_rows(
                            provider.fetch([symbol], "5m", start, end), [symbol], expected
                        )[symbol]
                        third = _validate_rows(
                            provider.fetch([symbol], "5m", start, end), [symbol], expected
                        )[symbol]
                        if not (_signature(first_map) == _signature(second) == _signature(third)):
                            raise main.CollectorError(
                                f"{symbol} 三次5m结果不一致，拒绝声明无成交窗口。"
                            )
                        confirmations = 3
                    for eob, row in first_map.items():
                        collected[(symbol, eob)] = _bar_payload(row)
                    proofs[symbol] = {
                        "symbol": symbol,
                        "missingEobs": [item.isoformat(timespec="seconds") for item in missing],
                        "confirmations": confirmations,
                    }
                except (main.CollectorError, KeyError, ValueError) as error:
                    failures[symbol] = str(main.sanitize_error(str(error)))

        bars = [collected[key] for key in sorted(collected)]
        maximum_batch = max(1, min(int(claim.get("maximumBarsPerBatch") or 2000), 2000))
        for offset in range(0, len(bars), maximum_batch):
            client.push_next_day_validation_batch(
                lease_token, bars[offset : offset + maximum_batch]
            )
        result = client.complete_next_day_validation_jobs(
            lease_token,
            [proofs[symbol] for symbol in sorted(proofs) if symbol not in failures],
            [{"symbol": symbol, "error": failures[symbol]} for symbol in sorted(failures)],
        )
        LOGGER.info(
            "next-day claim completed: run=%s date=%s symbols=%s bars=%s failures=%s result=%s",
            claim.get("runId"), validation_date, len(symbols), len(bars), len(failures), result,
        )
        return result
    except Exception as error:
        client.fail_next_day_validation_lease(
            lease_token,
            str(main.sanitize_error(str(error))),
            isinstance(error, main.ProviderUnavailableError),
        )
        raise
    finally:
        collected.clear()
        proofs.clear()
        failures.clear()


def run(settings: main.Settings, run_id: int) -> int:
    client = main.ApiClient(settings)
    provider = main.GmHistoryProvider(settings.gm_token)
    while True:
        claim = client.claim_next_day_validation_jobs(run_id, MAXIMUM_SYMBOLS_PER_CLAIM)
        if not claim.get("leaseToken"):
            summary = client.get_next_day_validation_run(run_id)
            LOGGER.info("next-day validation run finished: %s", summary)
            return 0 if summary.get("status") == "COMPLETED" else 1
        collect_claim(settings, client, provider, claim)


def entrypoint() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.local.json")
    parser.add_argument("--run-id", type=int)
    parser.add_argument("--date-from", type=date.fromisoformat)
    parser.add_argument("--date-to", type=date.fromisoformat)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    if args.run_id is None and (args.date_from is None or args.date_to is None):
        parser.error("必须提供 --run-id，或同时提供 --date-from/--date-to。")
    try:
        settings = main.settings_for_run(main.Settings.load(Path(args.config).resolve()), date.today())
        configure_logging(Path(settings.state_directory))
        client = main.ApiClient(settings)
        run_id = args.run_id
        if run_id is None:
            provider = main.GmHistoryProvider(settings.gm_token)
            calendar_start = args.date_from - timedelta(days=40)
            trading_dates = provider.common_trading_dates(calendar_start, args.date_to)
            if not any(item < args.date_from for item in trading_dates):
                raise main.CollectorError("官方交易日历没有覆盖dateFrom之前的门禁日期。")
            created = client.create_next_day_validation_run(
                args.date_from, args.date_to, args.apply, trading_dates
            )
            run_id = int(created["runId"])
            LOGGER.info("next-day validation run created: %s", created)
        return run(settings, run_id)
    except Exception as error:
        LOGGER.exception("next-day validation collector stopped: %s", main.sanitize_error(str(error)))
        return 1


if __name__ == "__main__":
    raise SystemExit(entrypoint())
