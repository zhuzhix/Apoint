"""Read-only formal GM verification for the three-response sparse-bar contract.

This command never constructs ApiClient and never writes WebAPI/MySQL/Redis state.
It prints only symbols, EOBs, row counts, and derived digest prefixes.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import date, datetime
from pathlib import Path

import main


def strict_bar_map(
    rows: list[dict], symbol: str, expected_eobs: set[datetime]
) -> dict[datetime, dict]:
    result: dict[datetime, dict] = {}
    for row in rows:
        actual_symbol = str(row.get("symbol", "")).strip().upper()
        if actual_symbol != symbol:
            continue
        eob = main.parse_plan_time(row.get("eob"))
        if eob not in expected_eobs:
            raise main.CollectorError(
                f"{symbol} 返回计划外 EOB: {eob.isoformat(timespec='seconds')}"
            )
        if eob in result:
            raise main.CollectorError(
                f"{symbol} 返回重复 EOB: {eob.isoformat(timespec='seconds')}"
            )
        result[eob] = row
    return result


def digest_prefix(bar_map: dict[datetime, dict]) -> str:
    material = main.sparse_bar_map_signature(bar_map)
    return hashlib.sha256(
        json.dumps(material, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    ).hexdigest()[:16]


def run(config_path: Path, trading_date: date, symbols: list[str]) -> list[dict]:
    settings = main.Settings.load(config_path)
    provider = main.GmHistoryProvider(settings.gm_token)
    window_start = datetime.combine(trading_date, datetime.min.time()).replace(
        hour=9, minute=30
    )
    window_end = datetime.combine(trading_date, datetime.min.time()).replace(hour=15)
    expected_eobs = main.planned_eobs("5m", window_start, window_end)

    normalized_symbols = [symbol.strip().upper() for symbol in symbols]
    initial_rows = provider.fetch(
        normalized_symbols, "5m", window_start, window_end
    )
    unexpected = sorted(
        {str(row.get("symbol", "")).strip().upper() for row in initial_rows}
        - set(normalized_symbols)
    )
    if unexpected:
        raise main.CollectorError(f"初次查询返回批次外证券: {unexpected}")

    output: list[dict] = []
    for symbol in normalized_symbols:
        maps = [strict_bar_map(initial_rows, symbol, expected_eobs)]
        for _ in range(main.SPARSE_CONFIRMATIONS_REQUIRED - 1):
            probe_rows = provider.fetch([symbol], "5m", window_start, window_end)
            returned = {
                str(row.get("symbol", "")).strip().upper() for row in probe_rows
            }
            if returned - {symbol}:
                raise main.CollectorError(
                    f"{symbol} 单股探针返回其他证券: {sorted(returned - {symbol})}"
                )
            maps.append(strict_bar_map(probe_rows, symbol, expected_eobs))

        signatures = [main.sparse_bar_map_signature(value) for value in maps]
        identical = all(value == signatures[0] for value in signatures[1:])
        missing = sorted(expected_eobs - set(maps[0]))
        output.append(
            {
                "symbol": symbol,
                "frequency": "5m",
                "counts": [len(value) for value in maps],
                "missingEobs": [
                    value.isoformat(timespec="seconds") for value in missing
                ],
                "confirmations": len(maps),
                "mappingsIdentical": identical,
                "mappingDigestPrefixes": [digest_prefix(value) for value in maps],
            }
        )
    return output


def main_entry() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--date", required=True, type=date.fromisoformat)
    parser.add_argument("--symbols", nargs="+", required=True)
    args = parser.parse_args()
    try:
        result = run(args.config.resolve(), args.date, args.symbols)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        if any(
            not item["mappingsIdentical"]
            or item["confirmations"] != main.SPARSE_CONFIRMATIONS_REQUIRED
            for item in result
        ):
            return 1
        return 0
    except (main.CollectorError, OSError, ValueError, KeyError) as error:
        print(json.dumps({"status": "failed", "error": str(error)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main_entry())
