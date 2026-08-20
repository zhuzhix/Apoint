from __future__ import annotations

import unittest
from datetime import date, datetime, time as clock_time, timedelta
from pathlib import Path

import main
import next_day_validation_worker as worker


def settings() -> main.Settings:
    return main.Settings(
        api_base_url="http://test.invalid",
        api_key="test-key",
        gm_token="test-token",
        poll_seconds=5,
        heartbeat_seconds=5,
        universe_refresh_seconds=300,
        symbols_per_sdk_request=20,
        max_push_bars=2000,
        request_timeout_seconds=5,
        provider="gm",
        collector_id="next-day-unit-test",
        state_directory=str(Path("runtime-test")),
    )


def rows(symbol: str, trading_date: date, missing: set[datetime] | None = None) -> list[dict]:
    missing = missing or set()
    start = datetime.combine(trading_date, clock_time(9, 30))
    end = datetime.combine(trading_date, clock_time(15, 0))
    result: list[dict] = []
    for index, eob in enumerate(sorted(main.planned_eobs("5m", start, end))):
        if eob in missing:
            continue
        result.append(
            {
                "symbol": symbol,
                "frequency": "5m",
                "bob": (eob - timedelta(minutes=5)).isoformat(timespec="seconds"),
                "eob": eob.isoformat(timespec="seconds"),
                "openPrice": 10.0,
                "highPrice": 10.2,
                "lowPrice": 9.8,
                "closePrice": 10.1,
                "preClose": 10.0,
                "volume": 1000,
                "amount": 10100.0,
                "sourceRowHash": f"{symbol}-{index}".encode().hex().ljust(64, "0")[:64],
            }
        )
    return result


class FakeProvider:
    def __init__(self, trading_date: date, missing: set[datetime] | None = None) -> None:
        self.trading_date = trading_date
        self.missing = missing or set()
        self.calls: list[list[str]] = []

    def fetch(self, symbols: list[str], frequency: str, start: datetime, end: datetime) -> list[dict]:
        del frequency, start, end
        self.calls.append(symbols)
        result: list[dict] = []
        for symbol in symbols:
            result.extend(rows(symbol, self.trading_date, self.missing if symbol == "SHSE.600001" else set()))
        return result


class FakeClient:
    def __init__(self) -> None:
        self.batches: list[list[dict]] = []
        self.completed: list[tuple[list[dict], list[dict]]] = []
        self.failed = False

    def push_next_day_validation_batch(self, lease: str, bars: list[dict]) -> dict:
        del lease
        self.batches.append(bars)
        return {"accepted": len(bars)}

    def complete_next_day_validation_jobs(self, lease: str, proofs: list[dict], failures: list[dict]) -> dict:
        del lease
        self.completed.append((proofs, failures))
        return {"status": "completed"}

    def fail_next_day_validation_lease(self, lease: str, error: str, provider_unavailable: bool) -> dict:
        del lease, error, provider_unavailable
        self.failed = True
        return {"status": "released"}


class NextDayValidationWorkerTests(unittest.TestCase):
    def test_complete_rows_are_uploaded_once(self) -> None:
        trading_date = date(2026, 8, 18)
        client = FakeClient()
        provider = FakeProvider(trading_date)
        result = worker.collect_claim(
            settings(), client, provider,
            {"runId": 1, "leaseToken": "lease", "validationTradingDate": trading_date.isoformat(),
             "symbols": ["SHSE.600001"], "maximumBarsPerBatch": 2000},
        )
        self.assertEqual("completed", result["status"])
        self.assertEqual(48, sum(len(batch) for batch in client.batches))
        self.assertEqual(1, client.completed[0][0][0]["confirmations"])
        self.assertEqual([], client.completed[0][0][0]["missingEobs"])
        self.assertFalse(client.failed)

    def test_sparse_window_requires_three_identical_reads(self) -> None:
        trading_date = date(2026, 8, 18)
        missing = {datetime.combine(trading_date, clock_time(10, 5))}
        client = FakeClient()
        provider = FakeProvider(trading_date, missing)
        worker.collect_claim(
            settings(), client, provider,
            {"runId": 2, "leaseToken": "lease", "validationTradingDate": trading_date.isoformat(),
             "symbols": ["SHSE.600001"], "maximumBarsPerBatch": 2000},
        )
        proof = client.completed[0][0][0]
        self.assertEqual(3, proof["confirmations"])
        self.assertEqual(["2026-08-18T10:05:00"], proof["missingEobs"])
        self.assertEqual(3, sum(call == ["SHSE.600001"] for call in provider.calls))


if __name__ == "__main__":
    unittest.main()
