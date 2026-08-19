from __future__ import annotations

import tempfile
import unittest
from datetime import date, datetime, timedelta
from pathlib import Path

import main
import wave_history_worker as wave


def settings(directory: Path) -> main.Settings:
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
        collector_id="wave-unit-test",
        state_directory=str(directory),
    )


class FakeClient:
    def __init__(self) -> None:
        self.batches: list[list[dict]] = []
        self.completed: list[tuple[str, list[dict]]] = []
        self.failed: list[tuple[str, bool]] = []

    def push_wave_bottom_batch(self, lease_token: str, bars: list[dict]) -> dict:
        self.batches.append(bars)
        return {"accepted": len(bars)}

    def complete_wave_bottom_jobs(self, lease_token: str, failures: list[dict]) -> dict:
        self.completed.append((lease_token, failures))
        return {"status": "completed"}

    def fail_wave_bottom_lease(
        self, lease_token: str, error: str, provider_unavailable: bool
    ) -> dict:
        del error
        self.failed.append((lease_token, provider_unavailable))
        return {"status": "released"}


class FakeProvider:
    def __init__(self) -> None:
        self.calendar_calls: list[tuple[date, int]] = []
        self.fetch_calls: list[list[str]] = []
        self.trading_dates: list[date] = []

    def completed_trading_dates(self, end_date: date, count: int) -> list[date]:
        self.calendar_calls.append((end_date, count))
        self.trading_dates = [end_date - timedelta(days=count - index) for index in range(count)]
        return self.trading_dates

    def fetch(
        self, symbols: list[str], frequency: str, start: datetime, end: datetime
    ) -> list[dict]:
        del start, end
        self.fetch_calls.append(symbols)
        self.assert_frequency = frequency
        rows: list[dict] = []
        for symbol in symbols:
            for trading_date in self.trading_dates:
                rows.append(
                    {
                        "symbol": symbol,
                        "frequency": "1d",
                        "bob": datetime.combine(trading_date, datetime.min.time())
                        .replace(hour=9, minute=30)
                        .isoformat(timespec="seconds"),
                        "eob": datetime.combine(trading_date, datetime.min.time())
                        .replace(hour=15)
                        .isoformat(timespec="seconds"),
                        "openPrice": 10.0,
                        "highPrice": 10.5,
                        "lowPrice": 9.5,
                        "closePrice": 10.2,
                        "preClose": 10.0,
                        "volume": 1000,
                        "amount": 10200.0,
                        "sourceRowHash": f"{len(rows):064x}"[-64:],
                    }
                )
        return rows


class WaveHistoryWorkerTests(unittest.TestCase):
    def test_duplicate_events_share_one_symbol_fetch_and_complete_independently(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            client = FakeClient()
            provider = FakeProvider()
            claim = {
                "leaseToken": "lease-1",
                "maximumBarsPerBatch": 2000,
                "jobs": [
                    {
                        "jobId": 1,
                        "eventId": 10,
                        "symbol": "SHSE.600000",
                        "dataEndDate": "2026-08-18",
                        "requiredDailyBars": 120,
                    },
                    {
                        "jobId": 2,
                        "eventId": 11,
                        "symbol": "SHSE.600000",
                        "dataEndDate": "2026-08-18",
                        "requiredDailyBars": 120,
                    },
                ],
            }

            result = wave.collect_claim(
                settings(Path(directory)), client, provider, claim
            )

            self.assertEqual("completed", result["status"])
            self.assertEqual([["SHSE.600000"]], provider.fetch_calls)
            self.assertEqual(120, sum(len(batch) for batch in client.batches))
            self.assertEqual([("lease-1", [])], client.completed)
            self.assertEqual([], client.failed)

    def test_more_than_two_hundred_distinct_symbols_is_fatal_before_sdk(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            client = FakeClient()
            provider = FakeProvider()
            claim = {
                "leaseToken": "lease-overflow",
                "jobs": [
                    {
                        "jobId": index,
                        "eventId": index,
                        "symbol": f"SHSE.{600000 + index:06d}",
                        "dataEndDate": "2026-08-18",
                        "requiredDailyBars": 120,
                    }
                    for index in range(201)
                ],
            }
            with self.assertRaises(main.CollectorFatalError):
                wave.collect_claim(settings(Path(directory)), client, provider, claim)
            self.assertEqual([], provider.fetch_calls)


if __name__ == "__main__":
    unittest.main()
