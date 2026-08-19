from __future__ import annotations

import io
import json
import tempfile
import time
import unittest
from http.client import RemoteDisconnected
from unittest.mock import patch
from concurrent.futures import Future, ProcessPoolExecutor
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from urllib.error import HTTPError

import main


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
        collector_id="unit-test",
        state_directory=str(directory),
    )


class FakeClient:
    def __init__(self, plan: dict) -> None:
        self.plan = plan
        self.completed: list[tuple[str, list[str], list[dict]]] = []
        self.aborted: list[tuple[str, str]] = []
        self.blacklisted: list[tuple[str, int]] = []
        self.heartbeats: list[dict] = []
        self.events: list[str] = []
        self.universe_payloads: list[dict] = []
        self.plan_dates: list[date | None] = []

    def synchronize_universe(self, payload: dict) -> dict:
        self.events.append("universe")
        self.universe_payloads.append(payload)
        return {
            "status": "completed",
            "tradingDate": payload["tradingDate"],
            "isTradingDay": payload["isTradingDay"],
            "totalSymbols": len(payload["symbols"]),
            "eligibleSymbols": len(payload["symbols"]),
            "universeVersion": "unit-test",
            "payloadHash": "unit-test-hash",
        }

    def get_plan(self, trading_date: date | None = None) -> dict:
        self.events.append("plan")
        self.plan_dates.append(trading_date)
        return self.plan

    def complete(
        self,
        cycle_id: str,
        symbols: list[str],
        sparse_manifest: list[dict] | None = None,
    ) -> None:
        self.completed.append((cycle_id, symbols, sparse_manifest or []))

    def abort(self, cycle_id: str, error: str) -> None:
        self.aborted.append((cycle_id, error))

    def report_blacklist(
        self, collector_id: str, symbol: str, reason: str, failure_count: int
    ) -> None:
        self.blacklisted.append((symbol, failure_count))

    def heartbeat(self, payload: dict) -> None:
        self.heartbeats.append(payload)


class ScriptedExecutor:
    def __init__(self, fail_times: int = 0, fatal: bool = False) -> None:
        self.fail_times = fail_times
        self.fatal = fatal
        self.calls = 0
        self.job_sizes: list[int] = []

    def submit(self, function, collector_settings, job) -> Future:
        del function, collector_settings
        self.calls += 1
        self.job_sizes.append(len(job.symbols))
        future: Future = Future()
        if self.fatal:
            future.set_exception(main.BrokenProcessPool("worker terminated"))
        elif self.calls <= self.fail_times:
            future.set_result(
                main.CollectionOutcome(
                    1000 + self.calls,
                    (),
                    {symbol: "sdk-test-failure" for symbol in job.symbols},
                    0,
                )
            )
        else:
            future.set_result(
                main.CollectionOutcome(
                    1000 + self.calls, tuple(job.symbols), {}, len(job.symbols)
                )
            )
        return future


def make_plan(symbols: list[str], trading_date: date | None = None) -> dict:
    trading_date = trading_date or main.china_today()
    date_text = trading_date.isoformat()
    return {
        "shouldCollect": True,
        "cycleId": "cycle-1",
        "tradingDate": date_text,
        "expectedSymbolCount": len(symbols),
        "symbols": [{"symbol": symbol} for symbol in symbols],
        "windows": [
            {
                "frequency": "5m",
                "from": f"{date_text}T09:30:00",
                "to": f"{date_text}T09:35:00",
            }
        ],
    }


def normalized_bar(
    symbol: str,
    eob: datetime,
    *,
    close: float = 10.0,
    source_hash: str = "official-row",
) -> dict:
    return {
        "symbol": symbol,
        "frequency": "5m",
        "bob": (eob - timedelta(minutes=5)).isoformat(timespec="seconds"),
        "eob": eob.isoformat(timespec="seconds"),
        "openPrice": 10.0,
        "highPrice": max(10.0, close),
        "lowPrice": min(10.0, close),
        "closePrice": close,
        "preClose": 9.9,
        "volume": 1000,
        "amount": 10000.0,
        "sourceRowHash": source_hash,
    }


class ApiClientTransportTests(unittest.TestCase):
    class RaisingOpener:
        def __init__(self, error: BaseException) -> None:
            self.error = error

        def open(self, request, timeout):
            del request, timeout
            raise self.error

    class StaticResponse:
        def __init__(self, body: bytes) -> None:
            self.body = body

        def __enter__(self):
            return self

        def __exit__(self, exception_type, exception, traceback) -> None:
            del exception_type, exception, traceback

        def read(self) -> bytes:
            return self.body

    class StaticOpener:
        def __init__(self, body: bytes) -> None:
            self.body = body

        def open(self, request, timeout):
            del request, timeout
            return ApiClientTransportTests.StaticResponse(self.body)

    class SequencedOpener:
        def __init__(self, outcomes) -> None:
            self.outcomes = list(outcomes)
            self.calls = 0

        def open(self, request, timeout):
            del request, timeout
            self.calls += 1
            outcome = self.outcomes.pop(0)
            if isinstance(outcome, BaseException):
                raise outcome
            return ApiClientTransportTests.StaticResponse(outcome)

    def client(self, temporary: str) -> main.ApiClient:
        return main.ApiClient(settings(Path(temporary)))

    def test_transient_connection_failures_map_to_collector_error(self) -> None:
        failures = (
            RemoteDisconnected("peer closed connection without response"),
            ConnectionResetError("connection reset by peer"),
            BrokenPipeError("broken pipe while sending request"),
        )
        with tempfile.TemporaryDirectory() as temporary:
            for failure in failures:
                with self.subTest(error=type(failure).__name__):
                    client = self.client(temporary)
                    client._opener = self.RaisingOpener(failure)
                    with patch.object(main.time, "sleep") as sleep:
                        with self.assertRaises(main.CollectorError) as captured:
                            client.get_status()
                    self.assertIs(failure, captured.exception.__cause__)
                    self.assertIn("无法连接 WebAPI", str(captured.exception))
                    self.assertEqual(3, sleep.call_count)

    def test_idempotent_batch_retries_transient_transport_and_succeeds(self) -> None:
        outcomes = [
            RemoteDisconnected("first"),
            ConnectionResetError("second"),
            b'{"accepted":1}',
        ]
        with tempfile.TemporaryDirectory() as temporary:
            client = self.client(temporary)
            opener = self.SequencedOpener(outcomes)
            client._opener = opener
            with patch.object(main.time, "sleep") as sleep:
                client.push_batch("cycle-retry", [{"symbol": "SHSE.600000"}])
        self.assertEqual(3, opener.calls)
        self.assertEqual([1.0, 2.0], [call.args[0] for call in sleep.call_args_list])

    def test_http_business_error_keeps_status_and_response_body(self) -> None:
        failure = HTTPError(
            "http://test.invalid/api/internal/pair-trend-collection/status",
            409,
            "Conflict",
            {},
            io.BytesIO(b'{"error":"cycle-conflict"}'),
        )
        with tempfile.TemporaryDirectory() as temporary:
            client = self.client(temporary)
            client._opener = self.RaisingOpener(failure)
            with self.assertRaises(main.CollectorError) as captured:
                client.get_status()
        self.assertIn("HTTP 409", str(captured.exception))
        self.assertIn("cycle-conflict", str(captured.exception))
        self.assertNotIn("无法连接 WebAPI", str(captured.exception))

    def test_invalid_json_is_not_misclassified_as_transport_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            client = self.client(temporary)
            client._opener = self.StaticOpener(b'{"status":')
            with self.assertRaises(json.JSONDecodeError):
                client.get_status()


class ProviderErrorClassificationTests(unittest.TestCase):
    class StructuredGmError(RuntimeError):
        def __init__(self, status: int, message: str) -> None:
            super().__init__(message)
            self.status = status
            self.message = message

    def test_structured_1026_is_provider_authentication_failure(self) -> None:
        error = self.StructuredGmError(1026, "opaque")
        mapped = main.map_provider_error("history", error)
        self.assertIsInstance(mapped, main.ProviderAuthenticationError)
        self.assertIn("1026", str(mapped))

    def test_text_1026_is_provider_authentication_failure(self) -> None:
        error = RuntimeError("GmError status 1026, message 更新令牌错误")
        self.assertTrue(main.is_provider_authentication_error(error))

    def test_vendor_invalid_token_message_is_authentication_failure(self) -> None:
        error = self.StructuredGmError(1000, "无效的token")
        mapped = main.map_provider_error("history", error)
        self.assertIsInstance(mapped, main.ProviderAuthenticationError)

    def test_structured_1001_terminal_disconnect_is_provider_unavailable(self) -> None:
        error = self.StructuredGmError(1001, "无法连接到终端服务")
        mapped = main.map_provider_error("history", error)
        self.assertIsInstance(mapped, main.ProviderTerminalUnavailableError)
        self.assertIn("同一已登录 Windows 用户会话", str(mapped))

    def test_text_only_terminal_disconnect_is_not_over_classified(self) -> None:
        error = self.StructuredGmError(1000, "无法连接到终端服务")
        mapped = main.map_provider_error("history", error)
        self.assertIs(type(mapped), main.CollectorError)

    def test_non_authentication_sdk_error_remains_regular_collector_error(self) -> None:
        error = self.StructuredGmError(1001, "bad request")
        mapped = main.map_provider_error("history", error)
        self.assertIs(type(mapped), main.CollectorError)


class CollectorStateStoreTests(unittest.TestCase):
    def test_replace_permission_error_retries_then_succeeds(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            denied = PermissionError(13, "state file temporarily locked")
            with patch.object(
                main.os, "replace", side_effect=[denied, denied, None]
            ) as replace, patch.object(main.time, "sleep") as sleep:
                count = state.record_failure("SHSE.600000", "temporary")
        self.assertEqual(1, count)
        self.assertEqual(3, replace.call_count)
        self.assertEqual(
            [
                ((main.STATE_REPLACE_RETRY_DELAYS_SECONDS[0],), {}),
                ((main.STATE_REPLACE_RETRY_DELAYS_SECONDS[1],), {}),
            ],
            sleep.call_args_list,
        )

    def test_permanent_replace_permission_error_is_fatal(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            denied = PermissionError(13, "state file permanently locked")
            with patch.object(
                main.os, "replace", side_effect=denied
            ) as replace, patch.object(main.time, "sleep") as sleep:
                with self.assertRaises(main.CollectorFatalError):
                    state.record_failure("SHSE.600000", "permanent")
        self.assertEqual(
            len(main.STATE_REPLACE_RETRY_DELAYS_SECONDS) + 1,
            replace.call_count,
        )
        self.assertEqual(len(main.STATE_REPLACE_RETRY_DELAYS_SECONDS), sleep.call_count)

    def test_batch_success_and_failures_persist_once(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            state.record_failure("SHSE.600000", "old-failure")
            failures = {
                f"SHSE.{600001 + index:06d}": f"new-failure-{index}"
                for index in range(main.SYMBOLS_PER_JOB)
            }
            with patch.object(state, "_save", wraps=state._save) as save:
                counts, blacklisted = state.apply_results(
                    ("SHSE.600000",),
                    failures,
                )
            self.assertEqual(1, save.call_count)
            self.assertEqual(main.SYMBOLS_PER_JOB, len(counts))
            self.assertTrue(all(count == 1 for count in counts.values()))
            self.assertFalse(blacklisted)
            self.assertEqual(0, state.failure_count("SHSE.600000"))
            self.assertEqual(1, state.failure_count("SHSE.600001"))
            self.assertEqual(1, state.failure_count("SHSE.600002"))


class SupervisorTests(unittest.TestCase):
    def test_backfill_timeout_override_does_not_change_current_day_settings(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            original = settings(Path(temporary))
            current = main.settings_for_run(original, None)
            historical = main.settings_for_run(
                original, main.china_today() - timedelta(days=1)
            )
        self.assertIs(current, original)
        self.assertEqual(5, current.request_timeout_seconds)
        self.assertEqual(180, historical.request_timeout_seconds)
        self.assertEqual(5, original.request_timeout_seconds)

    def test_one_shot_collector_error_returns_nonzero(self) -> None:
        class FailingSupervisor:
            def run_once(self):
                raise main.CollectorError("strict-backfill-failure")

        self.assertEqual(
            1,
            main.run_supervisor_loop(
                FailingSupervisor(), once=True, poll_seconds=5
            ),
        )

    def test_resident_loop_continues_after_recoverable_collection_failure(self) -> None:
        class StopLoop(BaseException):
            pass

        class RecoveringSupervisor:
            def __init__(self) -> None:
                self.calls = 0

            def run_once(self) -> None:
                self.calls += 1
                if self.calls == 1:
                    raise main.CollectorError("temporary-disconnect")

        supervisor = RecoveringSupervisor()
        with patch.object(main.time, "sleep", side_effect=[None, StopLoop()]):
            with self.assertRaises(StopLoop):
                main.run_supervisor_loop(
                    supervisor, once=False, poll_seconds=5
                )
        self.assertEqual(2, supervisor.calls)

    def test_provider_frequency_unavailable_uses_five_minute_backoff(self) -> None:
        class StopLoop(BaseException):
            pass

        class UnavailableSupervisor:
            def __init__(self) -> None:
                self.waited_seconds = None

            def run_once(self) -> None:
                raise main.ProviderFrequencyUnavailableError("1d-not-ready")

            def wait_for_provider_retry(self, seconds: int) -> None:
                self.waited_seconds = seconds
                raise StopLoop()

        supervisor = UnavailableSupervisor()
        with self.assertRaises(StopLoop):
            main.run_supervisor_loop(
                supervisor, once=False, poll_seconds=20
            )
        self.assertEqual(
            main.PROVIDER_FREQUENCY_RETRY_SECONDS,
            supervisor.waited_seconds,
        )

    def test_provider_backoff_keeps_degraded_heartbeat_alive(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                main.CollectorStateStore(Path(temporary)),
                ScriptedExecutor(),
                set(range(3901, 3907)),
            )
            with patch.object(
                main.time, "monotonic", side_effect=[0.0, 0.0, 5.0, 10.0]
            ), patch.object(main.time, "sleep") as sleep, patch.object(
                supervisor, "_heartbeat"
            ) as heartbeat:
                supervisor.wait_for_provider_retry(10)
        self.assertEqual(2, sleep.call_count)
        self.assertEqual(2, heartbeat.call_count)
        self.assertTrue(all(
            call.args[0] == "degraded" and call.kwargs["force"]
            for call in heartbeat.call_args_list
        ))

    def test_fatal_error_is_not_retried_by_resident_loop(self) -> None:
        class FatalSupervisor:
            def __init__(self) -> None:
                self.calls = 0

            def run_once(self) -> None:
                self.calls += 1
                raise main.CollectorFatalError("worker-pool-broken")

        supervisor = FatalSupervisor()
        with patch.object(main.time, "sleep") as sleep:
            result = main.run_supervisor_loop(
                supervisor, once=False, poll_seconds=5
            )
        self.assertEqual(1, result)
        self.assertEqual(1, supervisor.calls)
        sleep.assert_not_called()

    def test_partitions_never_exceed_two_hundred_and_complete_exact_universe(self) -> None:
        symbols = [f"SHSE.{index:06d}" for index in range(401)]
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            executor = ScriptedExecutor()
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)), state, executor, set(range(2001, 2007))
            )
            client = FakeClient(make_plan(symbols))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            self.assertTrue(supervisor.run_once())
            self.assertEqual([200, 200, 1], executor.job_sizes)
            self.assertTrue(all(size <= main.SYMBOLS_PER_JOB for size in executor.job_sizes))
            self.assertEqual(symbols, client.completed[0][1])
            self.assertFalse(client.aborted)
            self.assertEqual(6, client.heartbeats[-1]["activeProcesses"])
            self.assertEqual("2.2.8", client.heartbeats[-1]["version"])
            self.assertTrue(
                all(worker["pid"] is not None for worker in client.heartbeats[-1]["workers"])
            )

    def test_third_symbol_failure_blacklists_for_one_day_and_aborts_cycle(self) -> None:
        symbol = "SHSE.600000"
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            executor = ScriptedExecutor(fail_times=3)
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)), state, executor, set(range(3001, 3007))
            )
            client = FakeClient(make_plan([symbol]))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            self.assertFalse(supervisor.run_once())
            self.assertEqual(3, executor.calls)
            self.assertEqual([(symbol, 3)], client.blacklisted)
            self.assertEqual(1, len(client.aborted))
            self.assertFalse(client.completed)
            blacklist = state.active_blacklist()
            self.assertIn(symbol, blacklist)
            until = datetime.fromisoformat(blacklist[symbol]["until"])
            remaining = until - datetime.now(timezone.utc)
            self.assertGreater(remaining.total_seconds(), 23.9 * 60 * 60)

    def test_supervisor_forwards_worker_sparse_manifest_to_complete(self) -> None:
        symbol = "SHSE.600000"

        class SparseExecutor(ScriptedExecutor):
            def submit(self, function, collector_settings, job) -> Future:
                del function, collector_settings
                future: Future = Future()
                future.set_result(main.CollectionOutcome(
                    3101,
                    tuple(job.symbols),
                    {},
                    0,
                    (main.SparseBarManifest(
                        symbol,
                        "5m",
                        (f"{main.china_today().isoformat()}T09:35:00",),
                    ),),
                ))
                return future

        with tempfile.TemporaryDirectory() as temporary:
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                main.CollectorStateStore(Path(temporary)),
                SparseExecutor(),
                set(range(3101, 3107)),
            )
            client = FakeClient(make_plan([symbol]))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            self.assertTrue(supervisor.run_once())
            self.assertEqual(1, len(client.completed))
            self.assertEqual(3, client.completed[0][2][0]["confirmations"])
            self.assertEqual(symbol, client.completed[0][2][0]["symbol"])

    def test_pool_materializes_exactly_six_worker_processes(self) -> None:
        with ProcessPoolExecutor(max_workers=main.WORKER_PROCESS_COUNT) as executor:
            pids = main.warm_pool(executor)
        self.assertEqual(main.WORKER_PROCESS_COUNT, len(pids))

    def test_broken_pool_aborts_without_counting_symbol_failure(self) -> None:
        symbol = "SHSE.600000"
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                state,
                ScriptedExecutor(fatal=True),
                set(range(4001, 4007)),
            )
            client = FakeClient(make_plan([symbol]))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            with self.assertRaises(main.CollectorFatalError):
                supervisor.run_once()
            self.assertEqual(0, state.failure_count(symbol))
            self.assertEqual(1, len(client.aborted))
            self.assertFalse(client.blacklisted)

    def test_provider_frequency_unavailable_aborts_without_symbol_failure(self) -> None:
        symbol = "SHSE.600000"

        class ProviderUnavailableExecutor(ScriptedExecutor):
            def submit(self, function, collector_settings, job) -> Future:
                del function, collector_settings, job
                future: Future = Future()
                future.set_exception(main.ProviderFrequencyUnavailableError(
                    "1d:供应商频率/窗口暂不可用"
                ))
                return future

        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                state,
                ProviderUnavailableExecutor(),
                set(range(4101, 4107)),
            )
            client = FakeClient(make_plan([symbol]))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            with self.assertRaises(main.CollectorError):
                supervisor.run_once()
            self.assertEqual(0, state.failure_count(symbol))
            self.assertFalse(state.active_blacklist())
            self.assertFalse(client.blacklisted)
            self.assertEqual(1, len(client.aborted))
            self.assertEqual("degraded", client.heartbeats[-1]["status"])

    def test_provider_authentication_failure_aborts_without_symbol_failure(self) -> None:
        symbol = "SHSE.600000"

        class AuthenticationFailureExecutor(ScriptedExecutor):
            def submit(self, function, collector_settings, job) -> Future:
                del function, collector_settings, job
                future: Future = Future()
                future.set_exception(main.ProviderAuthenticationError(
                    "掘金 SDK 鉴权失败(status=1026)"
                ))
                return future

        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                state,
                AuthenticationFailureExecutor(),
                set(range(4201, 4207)),
            )
            client = FakeClient(make_plan([symbol]))
            supervisor.client = client
            supervisor._universe_synced_date = main.china_today()
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            with self.assertRaises(main.ProviderAuthenticationError):
                supervisor.run_once()
            self.assertEqual(0, state.failure_count(symbol))
            self.assertFalse(state.active_blacklist())
            self.assertFalse(client.blacklisted)
            self.assertEqual(1, len(client.aborted))
            self.assertEqual("degraded", client.heartbeats[-1]["status"])

    def test_fewer_than_six_workers_is_fatal_before_getting_plan(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                main.CollectorStateStore(Path(temporary)),
                ScriptedExecutor(),
                set(range(5001, 5006)),
            )
            client = FakeClient({"shouldCollect": False})
            supervisor.client = client
            with self.assertRaises(main.CollectorFatalError):
                supervisor.run_once()
            self.assertNotIn("plan", client.events)

    def test_universe_is_synchronized_before_plan(self) -> None:
        class SnapshotProvider:
            def validate_authorized_history(self):
                return None

            def fetch_authoritative_universe(self, requested: date):
                return main.AuthoritativeUniverseSnapshot(
                    requested, False, datetime.now(timezone.utc), ()
                )

        with tempfile.TemporaryDirectory() as temporary:
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                main.CollectorStateStore(Path(temporary)),
                ScriptedExecutor(),
                set(range(6001, 6007)),
            )
            client = FakeClient({"shouldCollect": False, "reason": "non-trading-day"})
            supervisor.client = client
            supervisor.universe_provider = SnapshotProvider()
            self.assertFalse(supervisor.run_once())
            self.assertLess(client.events.index("universe"), client.events.index("plan"))
            self.assertFalse(supervisor.run_once())
            self.assertEqual(1, client.events.count("universe"))
            supervisor._universe_synced_at_monotonic = (
                time.monotonic() - supervisor.settings.universe_refresh_seconds - 1
            )
            self.assertFalse(supervisor.run_once())
            self.assertEqual(2, client.events.count("universe"))

    def test_historical_backfill_uses_explicit_date_and_does_not_report_current_blacklist(self) -> None:
        target = main.china_today() - timedelta(days=1)
        symbol = "SHSE.600000"
        with tempfile.TemporaryDirectory() as temporary:
            state = main.CollectorStateStore(Path(temporary))
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                state,
                ScriptedExecutor(fail_times=3),
                set(range(7001, 7007)),
                target,
            )
            client = FakeClient(make_plan([symbol], target))
            supervisor.client = client
            supervisor._universe_synced_date = target
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()

            with self.assertRaises(main.CollectorError):
                supervisor.run_once()
            self.assertEqual([target], client.plan_dates)
            self.assertFalse(client.blacklisted)
            self.assertIn(symbol, state.active_blacklist())
            self.assertEqual(1, len(client.aborted))

    def test_historical_no_plan_is_a_nonzero_one_shot_failure(self) -> None:
        target = main.china_today() - timedelta(days=1)
        with tempfile.TemporaryDirectory() as temporary:
            supervisor = main.CollectorSupervisor(
                settings(Path(temporary)),
                main.CollectorStateStore(Path(temporary)),
                ScriptedExecutor(),
                set(range(8001, 8007)),
                target,
            )
            client = FakeClient({"shouldCollect": False, "reason": "strict-no-plan"})
            supervisor.client = client
            supervisor._universe_synced_date = target
            supervisor._universe_synced_at_monotonic = time.monotonic()
            supervisor._provider_authorized_at_monotonic = time.monotonic()
            self.assertEqual(
                1,
                main.run_supervisor_loop(
                    supervisor, once=True, poll_seconds=5
                ),
            )


class PartitionCompletenessTests(unittest.TestCase):
    def test_provider_authentication_failure_is_never_copied_to_symbols(self) -> None:
        symbols = ("SHSE.600000", "SHSE.600001")
        target = date(2026, 8, 17)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:35:00",
        }

        class Provider:
            def __init__(self, token):
                del token

            def fetch(self, requested, frequency, start, end):
                del requested, frequency, start, end
                raise main.ProviderAuthenticationError(
                    "掘金 SDK 鉴权失败(status=1026)"
                )

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(f"鉴权失败不得推送: {cycle_id}/{bars}")

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            with self.assertRaises(main.ProviderAuthenticationError):
                main.collect_partition(
                    settings(Path(temporary)),
                    main.CollectionJob("cycle-auth", (window,), symbols),
                )

    def test_inclusive_from_overlap_is_ignored_and_expected_bar_succeeds(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 18)
        old_eob = datetime(2026, 8, 18, 9, 35)
        expected_eob = datetime(2026, 8, 18, 9, 40)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:35:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        class Provider:
            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                return [
                    normalized_bar(symbol, old_eob, source_hash="old-overlap"),
                    normalized_bar(symbol, expected_eob, source_hash="expected"),
                ]

        class Client:
            batches: list[dict] = []

            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                del cycle_id
                Client.batches.extend(bars)

        Client.batches = []
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob("cycle-overlap", (window,), (symbol,)),
            )
        self.assertEqual((symbol,), outcome.succeeded_symbols)
        self.assertFalse(outcome.failures)
        self.assertFalse(outcome.sparse_manifest)
        self.assertEqual(1, outcome.pushed_bars)
        self.assertEqual(
            ["2026-08-18T09:40:00"],
            [row["eob"] for row in Client.batches],
        )

    def test_eob_earlier_than_from_remains_a_strict_failure(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 18)
        earlier_eob = datetime(2026, 8, 18, 9, 35)
        expected_eob = datetime(2026, 8, 18, 9, 45)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:40:00",
            "to": f"{target.isoformat()}T09:45:00",
        }

        class Provider:
            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                return [
                    normalized_bar(symbol, earlier_eob, source_hash="too-early"),
                    normalized_bar(symbol, expected_eob, source_hash="expected"),
                ]

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(
                    f"起点前历史行不得被静默忽略: {cycle_id}/{bars}"
                )

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob("cycle-too-early", (window,), (symbol,)),
            )
        self.assertFalse(outcome.succeeded_symbols)
        self.assertIn(
            "5m:非计划EOB:2026-08-18T09:35:00",
            outcome.failures[symbol],
        )
        self.assertEqual(0, outcome.pushed_bars)

    def test_future_and_illegal_in_window_eobs_remain_strict_failures(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 18)
        expected_eob = datetime(2026, 8, 18, 9, 40)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:35:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        for label, extra_eob in (
            ("future", datetime(2026, 8, 18, 9, 45)),
            ("illegal-in-window", datetime(2026, 8, 18, 9, 37)),
        ):
            class Provider:
                def __init__(self, token):
                    del token

                def fetch(self, symbols, frequency, start, end):
                    del symbols, frequency, start, end
                    return [
                        normalized_bar(symbol, expected_eob, source_hash="expected"),
                        normalized_bar(symbol, extra_eob, source_hash=label),
                    ]

            class Client:
                def __init__(self, collector_settings):
                    del collector_settings

                def push_batch(self, cycle_id, bars):
                    raise AssertionError(
                        f"含非计划 EOB 的证券不得推送: {cycle_id}/{bars}"
                    )

            with self.subTest(case=label), tempfile.TemporaryDirectory() as temporary, \
                    patch.object(main, "GmHistoryProvider", Provider), \
                    patch.object(main, "ApiClient", Client):
                outcome = main.collect_partition(
                    settings(Path(temporary)),
                    main.CollectionJob(f"cycle-{label}", (window,), (symbol,)),
                )
            self.assertFalse(outcome.succeeded_symbols)
            self.assertIn("非计划EOB", outcome.failures[symbol])
            self.assertEqual(0, outcome.pushed_bars)

    def test_overlap_exception_does_not_accept_wrong_frequency(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 18)
        old_eob = datetime(2026, 8, 18, 9, 35)
        expected_eob = datetime(2026, 8, 18, 9, 40)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:35:00",
            "to": f"{target.isoformat()}T09:40:00",
        }
        wrong_frequency_row = normalized_bar(
            symbol, old_eob, source_hash="wrong-frequency-overlap"
        )
        wrong_frequency_row["frequency"] = "30m"

        class Provider:
            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                return [
                    wrong_frequency_row,
                    normalized_bar(symbol, expected_eob, source_hash="expected"),
                ]

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(
                    f"错周期重叠行不得被忽略后推送: {cycle_id}/{bars}"
                )

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob("cycle-wrong-frequency", (window,), (symbol,)),
            )
        self.assertFalse(outcome.succeeded_symbols)
        self.assertIn("返回周期不一致:30m", outcome.failures[symbol])
        self.assertEqual(0, outcome.pushed_bars)

    def test_overlap_only_response_cannot_prove_sparse_or_hide_missing_bar(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 18)
        old_eob = datetime(2026, 8, 18, 9, 35)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:35:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        class Provider:
            calls = 0

            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                Provider.calls += 1
                return [normalized_bar(symbol, old_eob, source_hash="old-overlap")]

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(
                    f"只有重叠旧行时不得推送: {cycle_id}/{bars}"
                )

        Provider.calls = 0
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            with self.assertRaises(main.ProviderFrequencyUnavailableError):
                main.collect_partition(
                    settings(Path(temporary)),
                    main.CollectionJob("cycle-overlap-only", (window,), (symbol,)),
                )
        self.assertEqual(1, Provider.calls)

    def test_out_of_batch_symbol_fails_every_requested_symbol_for_retry(self) -> None:
        requested_symbol = "SHSE.600000"
        target = date(2026, 8, 17)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:35:00",
        }

        class Provider:
            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                return [
                    {
                        "symbol": "SHSE.999999",
                        "frequency": "5m",
                        "bob": f"{target.isoformat()}T09:30:00",
                        "eob": f"{target.isoformat()}T09:35:00",
                        "sourceRowHash": "out-of-batch",
                    }
                ]

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(
                    f"批次外证券不得推送: {cycle_id}/{bars}"
                )

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob(
                    "cycle-out-of-batch", (window,), (requested_symbol,)
                ),
            )
        self.assertFalse(outcome.succeeded_symbols)
        self.assertIn(requested_symbol, outcome.failures)
        self.assertIn("SDK返回批次外证券", outcome.failures[requested_symbol])

    def test_three_identical_reads_prove_sparse_without_synthesizing_missing_eob(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 17)
        eob = datetime(2026, 8, 17, 9, 35)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        class Provider:
            calls = 0

            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                Provider.calls += 1
                return [normalized_bar(symbol, eob, source_hash="row-0935")]

        class Client:
            batches: list[dict] = []

            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                del cycle_id
                Client.batches.extend(bars)

        Client.batches = []
        Provider.calls = 0
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob("cycle-eob", (window,), (symbol,)),
            )
        self.assertEqual((symbol,), outcome.succeeded_symbols)
        self.assertFalse(outcome.failures)
        self.assertEqual(3, Provider.calls)
        self.assertEqual(1, len(Client.batches))
        self.assertEqual("2026-08-17T09:35:00", Client.batches[0]["eob"])
        self.assertEqual(1, len(outcome.sparse_manifest))
        proof = outcome.sparse_manifest[0]
        self.assertEqual(symbol, proof.symbol)
        self.assertEqual("5m", proof.frequency)
        self.assertEqual(("2026-08-17T09:40:00",), proof.missing_eobs)
        self.assertEqual(3, proof.confirmations)

    def test_probe_ohlcv_or_hash_mismatch_is_a_symbol_failure_and_pushes_nothing(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 17)
        eob = datetime(2026, 8, 17, 9, 35)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        class Provider:
            calls = 0

            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                Provider.calls += 1
                if Provider.calls == 3:
                    return [normalized_bar(
                        symbol, eob, close=10.1, source_hash="changed-row"
                    )]
                return [normalized_bar(symbol, eob, source_hash="stable-row")]

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(f"不一致稀疏数据不得推送: {cycle_id}/{bars}")

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob("cycle-mismatch", (window,), (symbol,)),
            )
        self.assertFalse(outcome.succeeded_symbols)
        self.assertIn(symbol, outcome.failures)
        self.assertIn("三次实际bar映射不一致", outcome.failures[symbol])
        self.assertFalse(outcome.sparse_manifest)

    def test_whole_window_empty_response_is_not_sparse_proof(self) -> None:
        symbol = "SHSE.600000"
        target = date(2026, 8, 17)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:40:00",
        }

        class Provider:
            calls = 0

            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del symbols, frequency, start, end
                Provider.calls += 1
                return []

        class Client:
            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                raise AssertionError(f"供应商空响应不得推送: {cycle_id}/{bars}")

        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            with self.assertRaises(main.ProviderFrequencyUnavailableError):
                main.collect_partition(
                    settings(Path(temporary)),
                    main.CollectionJob("cycle-empty", (window,), (symbol,)),
                )
        self.assertEqual(1, Provider.calls)

    def test_empty_symbol_can_be_verified_when_same_initial_group_has_real_bar(self) -> None:
        liquid_symbol = "SHSE.600000"
        no_trade_symbol = "SHSE.600001"
        target = date(2026, 8, 17)
        eob = datetime(2026, 8, 17, 9, 35)
        window = {
            "frequency": "5m",
            "from": f"{target.isoformat()}T09:30:00",
            "to": f"{target.isoformat()}T09:35:00",
        }

        class Provider:
            calls: list[tuple[str, ...]] = []

            def __init__(self, token):
                del token

            def fetch(self, symbols, frequency, start, end):
                del frequency, start, end
                Provider.calls.append(tuple(symbols))
                if len(symbols) > 1:
                    return [normalized_bar(liquid_symbol, eob, source_hash="liquid")]
                self.assert_probe_is_no_trade(symbols)
                return []

            @staticmethod
            def assert_probe_is_no_trade(symbols):
                if symbols != [no_trade_symbol]:
                    raise AssertionError(f"意外单股探针: {symbols}")

        class Client:
            batches: list[dict] = []

            def __init__(self, collector_settings):
                del collector_settings

            def push_batch(self, cycle_id, bars):
                del cycle_id
                Client.batches.extend(bars)

        Provider.calls = []
        Client.batches = []
        with tempfile.TemporaryDirectory() as temporary, \
                patch.object(main, "GmHistoryProvider", Provider), \
                patch.object(main, "ApiClient", Client):
            outcome = main.collect_partition(
                settings(Path(temporary)),
                main.CollectionJob(
                    "cycle-no-trade", (window,), (liquid_symbol, no_trade_symbol)
                ),
            )
        self.assertEqual({liquid_symbol, no_trade_symbol}, set(outcome.succeeded_symbols))
        self.assertEqual(
            [(liquid_symbol, no_trade_symbol), (no_trade_symbol,), (no_trade_symbol,)],
            Provider.calls,
        )
        self.assertEqual([liquid_symbol], [row["symbol"] for row in Client.batches])
        self.assertEqual(1, len(outcome.sparse_manifest))
        self.assertEqual(no_trade_symbol, outcome.sparse_manifest[0].symbol)
        self.assertEqual(("2026-08-17T09:35:00",), outcome.sparse_manifest[0].missing_eobs)
        self.assertEqual(3, outcome.sparse_manifest[0].confirmations)

    def test_exact_planned_eobs_succeed(self) -> None:
        start = datetime(2026, 8, 17, 9, 30)
        end = datetime(2026, 8, 17, 15, 0)
        self.assertEqual(48, len(main.planned_eobs("5m", start, end)))
        self.assertEqual(8, len(main.planned_eobs("30m", start, end)))
        self.assertEqual(4, len(main.planned_eobs("60m", start, end)))
        self.assertEqual(1, len(main.planned_eobs("1d", start, end)))


class FakeGm:
    SEC_TYPE_STOCK = 1

    def __init__(self, trading_flags: dict[str, bool] | None = None) -> None:
        self.trading_flags = trading_flags or {"SHSE": True, "SZSE": True}
        self.instrument_calls: list[dict] = []
        self.instrument_info_calls: list[dict] = []
        self.history_instrument_calls: list[dict] = []
        self.rows: list[dict] = []
        for prefix in ("600", "601", "603", "605", "688"):
            for suffix in range(1000):
                symbol = f"SHSE.{prefix}{suffix:03d}"
                st_prefixes = ("ST", "*ST", "S*ST", "SST", "＊ST")
                is_formal_st_fixture = prefix == "600" and suffix < 203
                fixture_name = (
                    f"{st_prefixes[suffix % len(st_prefixes)]}测试{suffix:03d}"
                    if is_formal_st_fixture
                    else ("普通ST在中间" if symbol == "SHSE.600500" else symbol)
                )
                self.rows.append(
                    {
                        "symbol": symbol,
                        "sec_name": fixture_name,
                        "listed_date": "2020-01-01",
                        "delisted_date": "2038-01-01",
                        "is_suspended": 1 if symbol == "SHSE.600001" else 0,
                    }
                )
        self.rows.extend(
            [
                {"symbol": "SHSE.900001", "sec_name": "B股", "listed_date": "2020-01-01", "delisted_date": "2038-01-01", "is_suspended": 0},
                {"symbol": "SZSE.200001", "sec_name": "B股", "listed_date": "2020-01-01", "delisted_date": "2038-01-01", "is_suspended": 0},
                {"symbol": "SZSE.301999", "sec_name": "未来上市", "listed_date": "2099-01-01", "delisted_date": "2199-01-01", "is_suspended": 0},
                {"symbol": "SZSE.300999", "sec_name": "已经退市", "listed_date": "2020-01-01", "delisted_date": "2020-12-31", "is_suspended": 0},
            ]
        )

    def get_trading_dates(self, exchange, start_date, end_date):
        self.requested_date = start_date
        self.end_date = end_date
        return [start_date] if self.trading_flags[exchange] else []

    def get_instruments(self, **kwargs):
        self.instrument_calls.append(kwargs)
        rows = list(self.rows)
        if kwargs["skip_st"]:
            rows = [row for row in rows if row["symbol"] != "SHSE.600000"]
        if kwargs["skip_suspended"]:
            rows = [row for row in rows if not row["is_suspended"]]
        return rows

    def get_instrumentinfos(self, **kwargs):
        self.instrument_info_calls.append(kwargs)
        return list(self.rows)

    def get_history_instruments(self, **kwargs):
        self.history_instrument_calls.append(kwargs)
        requested = set(kwargs["symbols"])
        created_at = kwargs["start_date"]
        result = []
        for row in self.rows:
            symbol = row["symbol"]
            if symbol not in requested:
                continue
            result.append(
                {
                    "symbol": symbol,
                    "sec_level": 2 if symbol == "SHSE.600500" else 1,
                    "is_suspended": row["is_suspended"],
                    "created_at": created_at,
                }
            )
        return result


class UniverseTests(unittest.TestCase):
    def provider(self, gm: FakeGm) -> main.GmHistoryProvider:
        provider = main.GmHistoryProvider("unit-test-token")
        provider._gm = gm
        return provider

    def test_strict_current_day_shanghai_shenzhen_universe(self) -> None:
        gm = FakeGm()
        requested = main.china_today()
        snapshot = self.provider(gm).fetch_authoritative_universe(requested)

        self.assertTrue(snapshot.is_trading_day)
        self.assertEqual(5000, len(snapshot.symbols))
        by_symbol = {item["symbol"]: item for item in snapshot.symbols}
        self.assertEqual(203, sum(1 for item in snapshot.symbols if item["isSt"]))
        self.assertTrue(by_symbol["SHSE.600000"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600001"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600002"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600003"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600004"]["isSt"])
        self.assertFalse(by_symbol["SHSE.600500"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600001"]["isSuspended"])
        self.assertNotIn("SHSE.900001", by_symbol)
        self.assertNotIn("SZSE.200001", by_symbol)
        self.assertNotIn("SZSE.301999", by_symbol)
        self.assertNotIn("SZSE.300999", by_symbol)
        self.assertEqual(2, len(gm.instrument_calls))
        for call in gm.instrument_calls:
            self.assertEqual(["SHSE", "SZSE"], call["exchanges"])
            self.assertNotIn("BJSE", call["exchanges"])
            self.assertFalse(call["skip_suspended"])
            self.assertFalse(call["skip_st"])

    def test_non_trading_day_uploads_an_empty_universe(self) -> None:
        gm = FakeGm({"SHSE": False, "SZSE": False})
        snapshot = self.provider(gm).fetch_authoritative_universe(date(2026, 8, 23))
        self.assertFalse(snapshot.is_trading_day)
        self.assertEqual((), snapshot.symbols)
        self.assertFalse(gm.instrument_calls)

    def test_historical_universe_uses_exact_day_security_level_and_status(self) -> None:
        gm = FakeGm()
        requested = main.china_today() - timedelta(days=1)
        snapshot = self.provider(gm).fetch_authoritative_universe(requested)

        self.assertTrue(snapshot.is_trading_day)
        self.assertEqual(5000, len(snapshot.symbols))
        by_symbol = {item["symbol"]: item for item in snapshot.symbols}
        # Current names mark the first 203 fixtures ST, but historical sec_level
        # marks only 600500. The historical result must not borrow current names.
        self.assertFalse(by_symbol["SHSE.600000"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600500"]["isSt"])
        self.assertTrue(by_symbol["SHSE.600001"]["isSuspended"])
        self.assertEqual(2, len(gm.instrument_info_calls))
        self.assertEqual(4, len(gm.history_instrument_calls))
        self.assertTrue(
            all(call["start_date"] == requested.isoformat() for call in gm.history_instrument_calls)
        )
        self.assertTrue(
            all(call["end_date"] == requested.isoformat() for call in gm.history_instrument_calls)
        )
        self.assertTrue(
            all(len(call["symbols"]) <= 3000 for call in gm.history_instrument_calls)
        )

    def test_shanghai_shenzhen_calendar_disagreement_is_fatal_to_sync(self) -> None:
        gm = FakeGm({"SHSE": True, "SZSE": False})
        with self.assertRaises(main.CollectorError):
            self.provider(gm).fetch_authoritative_universe(main.china_today())

    def test_changing_consecutive_snapshots_are_rejected(self) -> None:
        class UnstableGm(FakeGm):
            def get_instruments(self, **kwargs):
                rows = super().get_instruments(**kwargs)
                if len(self.instrument_calls) == 2:
                    rows = [dict(row) for row in rows]
                    rows[0]["sec_name"] = "changed-during-read"
                return rows

        with self.assertRaises(main.CollectorError):
            self.provider(UnstableGm()).fetch_authoritative_universe(
                main.china_today()
            )

    def test_st_name_contract_variants(self) -> None:
        self.assertTrue(main.is_st_security_name("ST公司"))
        self.assertTrue(main.is_st_security_name("*ST公司"))
        self.assertTrue(main.is_st_security_name("S*ST公司"))
        self.assertTrue(main.is_st_security_name("SST公司"))
        self.assertTrue(main.is_st_security_name("＊ST公司"))
        self.assertFalse(main.is_st_security_name("普通ST公司"))

    def test_abnormally_all_suspended_snapshot_is_rejected(self) -> None:
        gm = FakeGm()
        for row in gm.rows:
            row["is_suspended"] = 1
        with self.assertRaises(main.CollectorError) as captured:
            self.provider(gm).fetch_authoritative_universe(date(2026, 8, 18))
        self.assertIn("最低要求 4500", str(captured.exception))


class BarNormalizationTests(unittest.TestCase):
    def test_official_daily_midnight_semantics_normalize_to_market_session(self) -> None:
        row = {
            "symbol": "SHSE.600000",
            "bob": "2026-08-17 00:00:00",
            "eob": "2026-08-17 00:00:00",
            "open": 10.0,
            "high": 10.2,
            "low": 9.9,
            "close": 10.1,
            "pre_close": 9.95,
            "volume": 1000,
            "amount": 10100,
        }
        normalized = main.GmHistoryProvider._normalize(row, "1d")
        self.assertEqual("2026-08-17T09:30:00", normalized["bob"])
        self.assertEqual("2026-08-17T15:00:00", normalized["eob"])
        self.assertTrue(normalized["sourceRowHash"])

    def test_abnormal_daily_time_is_rejected_instead_of_relabelled(self) -> None:
        row = {
            "symbol": "SHSE.600000",
            "bob": "2026-08-17 09:30:00",
            "eob": "2026-08-17 15:00:00",
            "open": 10.0,
            "high": 10.2,
            "low": 9.9,
            "close": 10.1,
            "pre_close": 9.95,
            "volume": 1000,
            "amount": 10100,
        }
        with self.assertRaises(main.CollectorError):
            main.GmHistoryProvider._normalize(row, "1d")


if __name__ == "__main__":
    unittest.main()
