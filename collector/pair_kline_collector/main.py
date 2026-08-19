"""Six-worker Python K-line collector for the pair-trend WebAPI.

The supervisor owns retry/blacklist state and the WebAPI cycle. Exactly six
long-lived worker processes fetch at most 200 symbols per job. Workers only
talk to the market-data SDK and WebAPI; Redis/MySQL are deliberately absent.
"""

from __future__ import annotations

import argparse
import hashlib
from http.client import IncompleteRead, RemoteDisconnected
import json
import logging
import os
import re
import socket
import sys
import time
import uuid
from collections import deque
from concurrent.futures import FIRST_COMPLETED, Future, ProcessPoolExecutor, wait
from concurrent.futures.process import BrokenProcessPool
from dataclasses import dataclass, replace
from datetime import date, datetime, timedelta, timezone
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener


WORKER_PROCESS_COUNT = 6
SYMBOLS_PER_JOB = 200
MAX_FAILURE_ATTEMPTS = 3
BLACKLIST_DURATION = timedelta(days=1)
BACKFILL_COMPUTE_TIMEOUT_SECONDS = 2 * 60 * 60
SPARSE_CONFIRMATIONS_REQUIRED = 3
STATE_REPLACE_RETRY_DELAYS_SECONDS = (0.05, 0.1, 0.2, 0.4, 0.8)
LOGGER = logging.getLogger("pair-kline-collector")
COLLECTOR_VERSION = "2.2.8"
PROVIDER_FREQUENCY_RETRY_SECONDS = 300
PROVIDER_AUTHORIZATION_REFRESH_SECONDS = 300
WEBAPI_TRANSPORT_RETRY_DELAYS_SECONDS = (1.0, 2.0, 4.0)
SECRET_PATTERN = re.compile(
    r"(?i)(password|passwd|pwd|token|authorization|api[_-]?key)\s*[:=]\s*[^\s,;]+"
)
STRICT_A_SHARE_PATTERN = re.compile(
    r"^(SHSE\.(600|601|603|605|688)|SZSE\.(000|001|002|003|300|301))[0-9]{3}$"
)
ST_SECURITY_NAME_PATTERN = re.compile(r"^(?:\*?ST|S\*ST|SST)", re.IGNORECASE)
CHINA_TIMEZONE = timezone(timedelta(hours=8), name="Asia/Shanghai")
MINIMUM_TRADING_DAY_SYMBOLS = 4_000
MAXIMUM_TRADING_DAY_SYMBOLS = 10_000
MINIMUM_ELIGIBLE_TRADING_DAY_SYMBOLS = 4_500

# urllib wraps most connection failures in URLError, but failures raised while
# reading a response body can escape as http.client/ConnectionError subclasses.
# Keep this list transport-only: HTTPError keeps its status/body handling and
# JSON decoding errors must still escape as protocol failures.
TRANSIENT_WEBAPI_TRANSPORT_ERRORS = (
    URLError,
    TimeoutError,
    RemoteDisconnected,
    ConnectionError,
    IncompleteRead,
)


class CollectorError(RuntimeError):
    """A failed collection is never silently converted to a partial cycle."""


class CollectorFatalError(CollectorError):
    """Infrastructure failure that requires the supervisor process to restart."""


class ProviderUnavailableError(CollectorError):
    """A shared upstream failure that must never be charged to symbols."""


class ProviderFrequencyUnavailableError(ProviderUnavailableError):
    """A whole provider frequency/window is unavailable; never blame symbols."""


class ProviderAuthenticationError(ProviderUnavailableError):
    """The vendor rejected or could not refresh the configured identity."""


class ProviderTerminalUnavailableError(ProviderUnavailableError):
    """The SDK cannot reach the interactive EastMoney terminal service."""


def is_provider_authentication_error(error: BaseException) -> bool:
    """Recognize GM authentication failures without relying only on message text."""
    current: BaseException | None = error
    visited: set[int] = set()
    while current is not None and id(current) not in visited:
        visited.add(id(current))
        for name in ("status", "code", "status_code", "error_code"):
            value = getattr(current, name, None)
            if str(value).strip() == "1026":
                return True
        detail = str(current).lower()
        vendor_message = str(getattr(current, "message", "")).lower()
        if (
            "status 1026" in detail
            or "status=1026" in detail
            or "更新令牌错误" in detail
            or "token refresh" in detail
            or (
                "token" in vendor_message
                and any(
                    marker in vendor_message
                    for marker in ("invalid", "expired", "无效", "过期")
                )
            )
        ):
            return True
        current = current.__cause__ or current.__context__
    return False


def is_provider_terminal_unavailable_error(error: BaseException) -> bool:
    """Recognize the structured GM terminal-service connectivity failure."""
    current: BaseException | None = error
    visited: set[int] = set()
    while current is not None and id(current) not in visited:
        visited.add(id(current))
        status = next(
            (
                getattr(current, name, None)
                for name in ("status", "code", "status_code", "error_code")
                if getattr(current, name, None) is not None
            ),
            None,
        )
        detail = f"{current} {getattr(current, 'message', '')}".lower()
        if str(status).strip() == "1001" and (
            "无法连接到终端服务" in detail
            or "cannot connect to terminal service" in detail
        ):
            return True
        current = current.__cause__ or current.__context__
    return False


def map_provider_error(context: str, error: BaseException) -> CollectorError:
    if is_provider_authentication_error(error):
        return ProviderAuthenticationError(
            f"{context}: 掘金 SDK 鉴权失败(status=1026，更新令牌错误)；"
            "本轮不会累计任何股票失败"
        )
    if is_provider_terminal_unavailable_error(error):
        return ProviderTerminalUnavailableError(
            f"{context}: 掘金 SDK 无法连接终端服务(status=1001)；"
            "请确认采集器与东财掘金终端运行在同一已登录 Windows 用户会话；"
            "本轮不会累计任何股票失败"
        )
    return CollectorError(f"{context}: {sanitize_error(error)}")


@dataclass(frozen=True)
class Settings:
    api_base_url: str
    api_key: str
    gm_token: str
    poll_seconds: int
    heartbeat_seconds: int
    universe_refresh_seconds: int
    symbols_per_sdk_request: int
    max_push_bars: int
    request_timeout_seconds: int
    provider: str
    collector_id: str
    state_directory: str

    @staticmethod
    def load(config_path: Path) -> "Settings":
        payload = json.loads(config_path.read_text(encoding="utf-8"))
        key = os.environ.get("PAIR_TREND_GATEWAY_KEY", "").strip()
        if not key:
            key = str(payload.get("gatewayApiKey", "")).strip()
        if not key or key.startswith("set-"):
            raise CollectorError(
                "缺少 WebAPI Gateway Key：请设置 PAIR_TREND_GATEWAY_KEY，"
                "或在私有 config.local.json 中填写 gatewayApiKey。"
            )

        gm_token = os.environ.get("ASTOCK_TOKEN", "").strip()
        if not gm_token:
            gm_token = str(payload.get("gmToken", "")).strip()
        if not gm_token or gm_token.startswith("set-"):
            raise CollectorError(
                "缺少掘金 Token：请设置 ASTOCK_TOKEN，或在私有 config.local.json 中填写 gmToken。"
            )

        api_base_url = str(payload.get("apiBaseUrl", "")).strip().rstrip("/")
        if not api_base_url:
            raise CollectorError("config.local.json 缺少 apiBaseUrl。")

        state_directory = Path(str(payload.get("stateDirectory", "runtime")))
        if not state_directory.is_absolute():
            state_directory = (config_path.parent / state_directory).resolve()

        collector_id = str(payload.get("collectorId", "")).strip()
        if not collector_id:
            collector_id = f"{socket.gethostname().lower()}-pair-kline"

        return Settings(
            api_base_url=api_base_url,
            api_key=key,
            gm_token=gm_token,
            poll_seconds=max(5, int(payload.get("pollSeconds", 20))),
            heartbeat_seconds=max(5, int(payload.get("heartbeatSeconds", 10))),
            universe_refresh_seconds=max(
                60, min(3600, int(payload.get("universeRefreshSeconds", 300)))
            ),
            symbols_per_sdk_request=max(
                1, min(50, int(payload.get("symbolsPerSdkRequest", 20)))
            ),
            max_push_bars=max(1, min(3000, int(payload.get("maxPushBars", 2000)))),
            request_timeout_seconds=max(
                5, int(payload.get("requestTimeoutSeconds", 30))
            ),
            provider=str(payload.get("provider", "gm")).lower(),
            collector_id=collector_id,
            state_directory=str(state_directory),
        )


class ApiClient:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        # The collector-to-WebAPI path is an explicitly configured control
        # plane endpoint. Bypass WinINET/FlClash proxy discovery so a local
        # desktop proxy cannot turn this private authenticated path into 502s.
        self._opener = build_opener(ProxyHandler({}))

    def get_plan(self, trading_date: date | None = None) -> dict[str, Any]:
        suffix = "" if trading_date is None else f"?tradingDate={trading_date.isoformat()}"
        return self._request(
            "GET",
            f"/api/internal/pair-trend-collection/plan{suffix}",
            retry_transport=True,
        )

    def get_status(self) -> dict[str, Any]:
        """Read-only connectivity/authentication check; never creates a cycle."""
        return self._request(
            "GET", "/api/internal/pair-trend-collection/status", retry_transport=True
        )

    def synchronize_universe(self, payload: dict[str, Any]) -> dict[str, Any]:
        return self._request(
            "POST",
            "/api/internal/pair-trend-collection/universe",
            payload,
            retry_transport=True,
        )

    def push_batch(self, cycle_id: str, bars: list[dict[str, Any]]) -> None:
        self._request(
            "POST",
            f"/api/internal/pair-trend-collection/cycles/{cycle_id}/batches",
            {"bars": bars},
            retry_transport=True,
        )

    def complete(
        self,
        cycle_id: str,
        symbols: list[str],
        sparse_manifest: list[dict[str, Any]] | None = None,
    ) -> None:
        self._request(
            "POST",
            f"/api/internal/pair-trend-collection/cycles/{cycle_id}/complete",
            {
                "completedSymbols": symbols,
                "failures": [],
                "sparseManifest": sparse_manifest or [],
            },
        )

    def abort(self, cycle_id: str, error: str) -> None:
        self._request(
            "POST",
            f"/api/internal/pair-trend-collection/cycles/{cycle_id}/abort",
            {"error": sanitize_error(error)},
        )

    def report_blacklist(
        self, collector_id: str, symbol: str, reason: str, failure_count: int
    ) -> None:
        self._request(
            "POST",
            "/api/internal/pair-trend-collection/blacklist",
            {
                "collectorId": collector_id,
                "symbol": symbol,
                "reason": sanitize_error(reason),
                "failureCount": failure_count,
            },
            retry_transport=True,
        )

    def heartbeat(self, payload: dict[str, Any]) -> None:
        self._request(
            "POST",
            "/api/internal/operations/collector-heartbeat",
            payload,
            retry_transport=True,
        )

    def claim_wave_bottom_jobs(self, maximum_symbols: int = 200) -> dict[str, Any]:
        return self._request(
            "GET",
            "/api/internal/pair-trend-collection/wave-bottom/jobs/claim?" + urlencode(
                {
                    "collectorId": self._settings.collector_id,
                    "maxSymbols": maximum_symbols,
                }
            ),
            retry_transport=True,
        )

    def push_wave_bottom_batch(
        self, lease_token: str, bars: list[dict[str, Any]]
    ) -> dict[str, Any]:
        return self._request(
            "POST",
            "/api/internal/pair-trend-collection/wave-bottom/jobs/batches",
            {"leaseToken": lease_token, "bars": bars},
            retry_transport=True,
        )

    def complete_wave_bottom_jobs(
        self, lease_token: str, failures: list[dict[str, str]]
    ) -> dict[str, Any]:
        return self._request(
            "POST",
            "/api/internal/pair-trend-collection/wave-bottom/jobs/complete",
            {"leaseToken": lease_token, "failures": failures},
            retry_transport=True,
        )

    def fail_wave_bottom_lease(
        self, lease_token: str, error: str, provider_unavailable: bool
    ) -> dict[str, Any]:
        return self._request(
            "POST",
            "/api/internal/pair-trend-collection/wave-bottom/jobs/fail",
            {
                "leaseToken": lease_token,
                "error": sanitize_error(error),
                "providerUnavailable": provider_unavailable,
            },
            retry_transport=True,
        )

    def _request(
        self,
        method: str,
        path: str,
        body: dict[str, Any] | None = None,
        *,
        retry_transport: bool = False,
    ) -> dict[str, Any]:
        encoded = (
            None
            if body is None
            else json.dumps(
                body, ensure_ascii=False, separators=(",", ":")
            ).encode("utf-8")
        )
        retry_delays = WEBAPI_TRANSPORT_RETRY_DELAYS_SECONDS if retry_transport else ()
        for attempt in range(len(retry_delays) + 1):
            request = Request(
                self._settings.api_base_url + path,
                data=encoded,
                method=method,
                headers={
                    "X-Collector-Gateway-Key": self._settings.api_key,
                    "Accept": "application/json",
                    "Content-Type": "application/json",
                },
            )
            try:
                with self._opener.open(
                    request, timeout=self._settings.request_timeout_seconds
                ) as response:
                    text = response.read().decode("utf-8")
                    return {} if not text else json.loads(text)
            except HTTPError as error:
                detail = error.read().decode("utf-8", errors="replace")
                raise CollectorError(
                    f"WebAPI {method} {path} 返回 HTTP {error.code}: {detail}"
                ) from error
            except TRANSIENT_WEBAPI_TRANSPORT_ERRORS as error:
                if attempt >= len(retry_delays):
                    reason = getattr(error, "reason", error)
                    raise CollectorError(f"无法连接 WebAPI: {reason}") from error
                delay = retry_delays[attempt]
                LOGGER.warning(
                    "WebAPI transport failed; retrying %s %s in %.1fs (%s/%s): %s",
                    method,
                    path,
                    delay,
                    attempt + 1,
                    len(retry_delays),
                    sanitize_error(getattr(error, "reason", error)),
                )
                time.sleep(delay)
        raise AssertionError("unreachable WebAPI retry state")


@dataclass(frozen=True)
class AuthoritativeUniverseSnapshot:
    trading_date: date
    is_trading_day: bool
    source_updated_at: datetime
    symbols: tuple[dict[str, Any], ...]


class GmHistoryProvider:
    """Compatibility adapter around the vendor SDK history endpoints."""

    _frequency = {"5m": "300s", "30m": "1800s", "60m": "3600s", "1d": "1d"}
    _fields = "symbol,bob,eob,open,high,low,close,pre_close,volume,amount"

    def __init__(self, token: str) -> None:
        self._token = token
        self._gm: Any = None

    def _load_sdk(self) -> Any:
        if self._gm is not None:
            return self._gm
        try:
            import gm.api as gm  # type: ignore[import-not-found]
        except ImportError as error:
            raise CollectorError("未安装东方财富掘金 gm SDK，无法执行正式采集。") from error
        set_token = getattr(gm, "set_token", None)
        if set_token is None:
            raise CollectorError("当前 gm SDK 不提供 set_token，无法确认采集身份。")
        try:
            set_token(self._token)
        except Exception as error:
            raise map_provider_error("gm.set_token 失败", error) from error
        self._gm = gm
        return gm

    def fetch(
        self,
        symbols: list[str],
        frequency: str,
        start: datetime,
        end: datetime,
    ) -> list[dict[str, Any]]:
        gm = self._load_sdk()
        kwargs = {
            "frequency": self._frequency[frequency],
            "start_time": start.strftime("%Y-%m-%d %H:%M:%S"),
            "end_time": end.strftime("%Y-%m-%d %H:%M:%S"),
            "fields": self._fields,
            "adjust": getattr(gm, "ADJUST_NONE", None),
            "df": True,
        }
        errors: list[Exception] = []
        # history_n requires a count and cannot represent this start/end
        # window. Use the matching API only; invalid compatibility attempts
        # would add latency and obscure a real deployment mismatch.
        attempts = (("history", {"symbol": ",".join(symbols)}),)
        for method_name, symbol_argument in attempts:
            method = getattr(gm, method_name, None)
            if method is None:
                continue
            try:
                result = method(**kwargs, **symbol_argument)
                return [self._normalize(row, frequency) for row in self._rows(result)]
            except Exception as error:
                if is_provider_authentication_error(error):
                    raise map_provider_error(
                        f"掘金 SDK {frequency} 批量 history 调用失败", error
                    ) from error
                if isinstance(error, (TypeError, ValueError, RuntimeError)):
                    errors.append(error)
                    continue
                raise CollectorError(
                    f"掘金 SDK {frequency} 批量 history 调用异常: "
                    f"{type(error).__name__}:{sanitize_error(error)}"
                ) from error
        reason = "; ".join(str(error) for error in errors[-3:])
        raise CollectorError(f"掘金 SDK {frequency} 批量 history 调用失败: {reason}")

    def validate(self) -> None:
        """Import the SDK and apply the token without requesting market data."""
        gm = self._load_sdk()
        required = (
            "get_trading_dates",
            "get_instruments",
            "get_instrumentinfos",
            "get_history_instruments",
        )
        missing = [name for name in required if not callable(getattr(gm, name, None))]
        if missing:
            raise CollectorError(
                f"当前 gm SDK 缺少权威股票池所需接口: {','.join(missing)}"
            )

    def completed_trading_dates(self, end_date: date, count: int) -> list[date]:
        """Return an exact common SHSE/SZSE calendar ending no later than end_date."""
        if count < 1 or count > 240:
            raise CollectorError(f"日K交易日数量无效: {count}")
        gm = self._load_sdk()
        start_date = end_date - timedelta(days=max(400, count * 3))
        calendars: dict[str, list[date]] = {}
        for exchange in ("SHSE", "SZSE"):
            try:
                values = gm.get_trading_dates(
                    exchange=exchange,
                    start_date=start_date.isoformat(),
                    end_date=end_date.isoformat(),
                )
            except Exception as error:
                raise map_provider_error(
                    f"gm.get_trading_dates({exchange}) 波段日K日历失败", error
                ) from error
            calendars[exchange] = sorted(
                {
                    parsed
                    for value in values or []
                    if (parsed := optional_date(value)) is not None
                    and parsed <= end_date
                }
            )
        if calendars["SHSE"] != calendars["SZSE"]:
            raise CollectorError("gm 沪深波段日K交易日历不一致，拒绝使用近似日期。")
        if len(calendars["SHSE"]) < count:
            raise ProviderFrequencyUnavailableError(
                f"截止 {end_date.isoformat()} 只取得 {len(calendars['SHSE'])}/{count} 个交易日"
            )
        return calendars["SHSE"][-count:]

    def validate_authorized_history(self) -> None:
        """Prove that the token can execute the same history API used by workers."""
        gm = self._load_sdk()
        today = china_today()
        start_date = today - timedelta(days=14)
        try:
            values = gm.get_trading_dates(
                exchange="SHSE",
                start_date=start_date.isoformat(),
                end_date=today.isoformat(),
            )
        except Exception as error:
            raise map_provider_error("gm history 鉴权预检交易日查询失败", error) from error
        completed_dates = sorted(
            {
                parsed
                for value in values or []
                if (parsed := optional_date(value)) is not None and parsed < today
            }
        )
        if not completed_dates:
            raise ProviderFrequencyUnavailableError(
                "gm history 鉴权预检找不到最近14日内已收盘交易日"
            )
        probe_date = completed_dates[-1]
        probe_start = datetime.combine(probe_date, datetime.min.time()).replace(
            hour=9, minute=30
        )
        probe_end = probe_start.replace(minute=35)
        rows = self.fetch(
            ["SHSE.600000", "SZSE.000001"], "5m", probe_start, probe_end
        )
        if not rows:
            raise ProviderFrequencyUnavailableError(
                f"gm history 鉴权预检在 {probe_date.isoformat()} 没有返回样本K线"
            )

    def fetch_authoritative_universe(
        self, trading_date: date
    ) -> AuthoritativeUniverseSnapshot:
        """Read the exact requested-day SHSE/SZSE A-share universe from gm.

        Today uses the current instrument snapshot. A past-date backfill uses
        listing metadata plus get_history_instruments for that exact date's
        sec_level/is_suspended. No current or previous-day state is substituted.
        """
        gm = self._load_sdk()
        self.validate()
        date_text = trading_date.isoformat()
        market_flags: dict[str, bool] = {}
        for exchange in ("SHSE", "SZSE"):
            try:
                values = gm.get_trading_dates(
                    exchange=exchange, start_date=date_text, end_date=date_text
                )
            except Exception as error:
                raise map_provider_error(
                    f"gm.get_trading_dates({exchange},{date_text}) 失败", error
                ) from error
            returned_dates = {
                parsed
                for value in values or []
                if (parsed := optional_date(value)) is not None
            }
            unexpected = returned_dates - {trading_date}
            if unexpected:
                raise CollectorError(
                    f"gm {exchange} 交易日接口返回了请求范围外日期: {sorted(unexpected)}"
                )
            market_flags[exchange] = trading_date in returned_dates

        if market_flags["SHSE"] != market_flags["SZSE"]:
            raise CollectorError(
                f"gm 沪深交易日判断不一致: SHSE={market_flags['SHSE']},"
                f"SZSE={market_flags['SZSE']}"
            )
        source_updated_at = datetime.now(timezone.utc)
        if not market_flags["SHSE"]:
            return AuthoritativeUniverseSnapshot(
                trading_date, False, source_updated_at, ()
            )

        sec_type_stock = getattr(gm, "SEC_TYPE_STOCK", 1)
        # A snapshot changing while it is read must never be submitted as a
        # half-old/half-new universe. Two independently normalized rounds must
        # be byte-for-byte identical; otherwise the next supervisor poll retries.
        snapshot_reader = (
            self._fetch_historical_universe_snapshot
            if trading_date < china_today()
            else self._fetch_current_universe_snapshot
        )
        first = snapshot_reader(gm, trading_date, date_text, sec_type_stock)
        second = snapshot_reader(gm, trading_date, date_text, sec_type_stock)
        first_hash = universe_payload_hash(first)
        second_hash = universe_payload_hash(second)
        if first_hash != second_hash:
            raise CollectorError(
                f"gm {date_text} 股票池连续两轮规范化快照不一致，拒绝提交半快照；"
                f"first={first_hash[:12]},second={second_hash[:12]}"
            )
        symbols = second
        return AuthoritativeUniverseSnapshot(
            trading_date, True, datetime.now(timezone.utc), tuple(symbols)
        )

    def _fetch_current_universe_snapshot(
        self, gm: Any, trading_date: date, date_text: str, sec_type_stock: Any
    ) -> list[dict[str, Any]]:
        common = {
            # Deliberately exclude BJSE: this project and API contract are
            # strictly limited to Shanghai/Shenzhen A shares.
            "exchanges": ["SHSE", "SZSE"],
            "sec_types": [sec_type_stock],
            "df": True,
        }
        try:
            all_result = gm.get_instruments(
                **common,
                skip_suspended=False,
                skip_st=False,
                fields="symbol,sec_name,listed_date,delisted_date,is_suspended",
            )
        except Exception as error:
            raise map_provider_error(
                f"gm.get_instruments({date_text}) 当前沪深股票快照失败", error
            ) from error

        all_rows: dict[str, dict[str, Any]] = {}
        for row in self._rows(all_result):
            if not is_strict_a_share(row.get("symbol")):
                continue
            symbol = normalize_symbol(row.get("symbol"))
            if symbol in all_rows:
                raise CollectorError(f"gm 当前股票快照包含重复证券 {symbol}")
            all_rows[symbol] = row
        active_rows: dict[str, tuple[dict[str, Any], date | None, date | None]] = {}
        for symbol, row in all_rows.items():
            list_date = optional_date(row.get("listed_date"))
            delist_date = optional_date(row.get("delisted_date"))
            if list_date is not None and list_date > trading_date:
                continue
            if delist_date is not None and delist_date < trading_date:
                continue
            active_rows[symbol] = (row, list_date, delist_date)

        symbols: list[dict[str, Any]] = []
        for symbol, (row, list_date, delist_date) in active_rows.items():
            name = str(row.get("sec_name") or "").strip()
            if not name:
                raise CollectorError(f"gm 当前股票快照中 {symbol} 缺少证券名称")
            # gm SDK 3.0.186 has been observed returning active ST securities
            # even with skip_st=True. The authoritative current name prefix is
            # therefore the strict source for the ST flag.
            is_st = is_st_security_name(name)
            is_suspended = strict_bool(
                row.get("is_suspended"), f"{symbol}.is_suspended"
            )
            symbols.append(
                {
                    "symbol": symbol,
                    "name": name,
                    "isSt": is_st,
                    "isSuspended": is_suspended,
                    "listDate": list_date.isoformat() if list_date else None,
                    "delistDate": delist_date.isoformat() if delist_date else None,
                }
            )

        symbols.sort(key=lambda item: item["symbol"])
        if not MINIMUM_TRADING_DAY_SYMBOLS <= len(symbols) <= MAXIMUM_TRADING_DAY_SYMBOLS:
            raise CollectorError(
                "gm 当前交易日严格沪深 A 股快照数量异常: "
                f"{len(symbols)}，要求 {MINIMUM_TRADING_DAY_SYMBOLS}-"
                f"{MAXIMUM_TRADING_DAY_SYMBOLS}；禁止以前一日列表兜底。"
            )
        eligible_count = sum(
            1 for item in symbols if not item["isSt"] and not item["isSuspended"]
        )
        if eligible_count < MINIMUM_ELIGIBLE_TRADING_DAY_SYMBOLS:
            raise CollectorError(
                "gm 当前交易日可采集沪深 A 股数量异常: "
                f"{eligible_count}，最低要求 "
                f"{MINIMUM_ELIGIBLE_TRADING_DAY_SYMBOLS}；拒绝提交股票池。"
            )
        return symbols

    def _fetch_historical_universe_snapshot(
        self, gm: Any, trading_date: date, date_text: str, sec_type_stock: Any
    ) -> list[dict[str, Any]]:
        """Build a point-in-time universe without borrowing today's status."""
        try:
            instrument_result = gm.get_instrumentinfos(
                exchanges=["SHSE", "SZSE"],
                sec_types=[sec_type_stock],
                fields="symbol,sec_name,listed_date,delisted_date",
                df=True,
            )
        except Exception as error:
            raise map_provider_error(
                f"gm.get_instrumentinfos({date_text}) 沪深股票元数据失败", error
            ) from error

        candidates: dict[str, tuple[str, date | None, date | None]] = {}
        for row in self._rows(instrument_result):
            if not is_strict_a_share(row.get("symbol")):
                continue
            symbol = normalize_symbol(row.get("symbol"))
            if symbol in candidates:
                raise CollectorError(f"gm 历史股票元数据包含重复证券 {symbol}")
            listed_date = optional_date(row.get("listed_date"))
            delisted_date = optional_date(row.get("delisted_date"))
            if listed_date is not None and listed_date > trading_date:
                continue
            if delisted_date is not None and delisted_date < trading_date:
                continue
            name = str(row.get("sec_name") or row.get("name") or "").strip()
            if not name:
                raise CollectorError(f"gm 历史股票元数据中 {symbol} 缺少证券名称")
            candidates[symbol] = (name, listed_date, delisted_date)

        if not MINIMUM_TRADING_DAY_SYMBOLS <= len(candidates) <= MAXIMUM_TRADING_DAY_SYMBOLS:
            raise CollectorError(
                f"gm {date_text} 上市/退市点时沪深 A 股候选数量异常: "
                f"{len(candidates)}，要求 {MINIMUM_TRADING_DAY_SYMBOLS}-"
                f"{MAXIMUM_TRADING_DAY_SYMBOLS}；禁止使用当前股票池兜底。"
            )

        historical_rows: dict[str, dict[str, Any]] = {}
        # 官方接口单次最多返回 3300 行；3000 留出明确余量，且每批只请求一天。
        for symbol_batch in chunks(sorted(candidates), 3_000):
            try:
                result = gm.get_history_instruments(
                    symbols=symbol_batch,
                    start_date=date_text,
                    end_date=date_text,
                    fields="symbol,sec_level,is_suspended,created_at",
                    df=True,
                )
            except Exception as error:
                raise map_provider_error(
                    f"gm.get_history_instruments({date_text}) 点时状态失败", error
                ) from error
            requested = set(symbol_batch)
            for row in self._rows(result):
                symbol = normalize_symbol(row.get("symbol"))
                if symbol not in requested:
                    raise CollectorError(
                        f"gm {date_text} 历史状态返回了批次外证券 {symbol}"
                    )
                if symbol in historical_rows:
                    raise CollectorError(
                        f"gm {date_text} 历史状态包含重复证券 {symbol}"
                    )
                created_at = optional_date(row.get("created_at"))
                if created_at is not None and created_at != trading_date:
                    raise CollectorError(
                        f"gm {symbol} 历史状态日期 {created_at} 与请求 {date_text} 不一致"
                    )
                historical_rows[symbol] = row

        missing = sorted(set(candidates) - set(historical_rows))
        if missing:
            raise CollectorError(
                f"gm {date_text} 历史状态缺少 {len(missing)} 只点时证券，"
                f"示例:{missing[:10]}；禁止用当前状态填补。"
            )

        symbols: list[dict[str, Any]] = []
        security_level_counts: dict[int, int] = {}
        for symbol in sorted(candidates):
            name, listed_date, delisted_date = candidates[symbol]
            row = historical_rows[symbol]
            try:
                security_level = int(row["sec_level"])
            except (KeyError, TypeError, ValueError) as error:
                raise CollectorError(
                    f"gm {symbol} 缺少有效历史 sec_level"
                ) from error
            security_level_counts[security_level] = (
                security_level_counts.get(security_level, 0) + 1
            )
            # 官方定义只有 1 是正常证券；2/3 为 ST，5 为退市整理期，
            # 12/13 为风险警示/退市整理，其余未知级别也必须保守排除。
            is_st = security_level != 1
            is_suspended = strict_bool(
                row.get("is_suspended"), f"{symbol}.is_suspended"
            )
            symbols.append(
                {
                    "symbol": symbol,
                    "name": name,
                    "isSt": is_st,
                    "isSuspended": is_suspended,
                    "listDate": listed_date.isoformat() if listed_date else None,
                    "delistDate": delisted_date.isoformat() if delisted_date else None,
                }
            )

        eligible_count = sum(
            1 for item in symbols if not item["isSt"] and not item["isSuspended"]
        )
        if eligible_count < MINIMUM_ELIGIBLE_TRADING_DAY_SYMBOLS:
            raise CollectorError(
                f"gm {date_text} 可采集沪深 A 股数量异常: {eligible_count}，最低要求 "
                f"{MINIMUM_ELIGIBLE_TRADING_DAY_SYMBOLS}；拒绝提交历史股票池。"
            )
        LOGGER.info(
            "historical universe validated: date=%s candidates=%s rows=%s "
            "missing=0 duplicate=0 dateMismatch=0 secLevel=%s suspended=%s eligible=%s",
            date_text,
            len(candidates),
            len(historical_rows),
            ",".join(
                f"{level}:{count}"
                for level, count in sorted(security_level_counts.items())
            ),
            sum(1 for item in symbols if item["isSuspended"]),
            eligible_count,
        )
        return symbols

    @staticmethod
    def _rows(result: Any) -> Iterable[dict[str, Any]]:
        if result is None:
            return []
        if hasattr(result, "to_dict"):
            return result.to_dict("records")
        if isinstance(result, dict):
            return [result]
        return result

    @staticmethod
    def _normalize(row: dict[str, Any], frequency: str) -> dict[str, Any]:
        def get(*names: str) -> Any:
            for name in names:
                if name in row and row[name] is not None:
                    return row[name]
            return None

        symbol = str(get("symbol")).strip().upper()
        if not symbol:
            raise CollectorError("SDK 返回了没有 symbol 的 K 线。")
        bob = iso_datetime(get("bob", "begin_time"))
        eob = iso_datetime(get("eob", "end_time"))
        if frequency == "1d":
            daily_bob = datetime.fromisoformat(bob)
            daily_eob = datetime.fromisoformat(eob)
            if (
                daily_bob.date() != daily_eob.date()
                or daily_bob.time() != datetime.min.time()
                or daily_eob.time() != datetime.min.time()
            ):
                raise CollectorError(
                    f"SDK 日 K 线 {symbol} 返回异常 bob/eob: {bob}/{eob}；"
                    "只接受官方同交易日午夜语义。"
                )
            bob = datetime.combine(daily_bob.date(), datetime.min.time()).replace(
                hour=9, minute=30
            ).isoformat(timespec="seconds")
            eob = datetime.combine(daily_eob.date(), datetime.min.time()).replace(
                hour=15
            ).isoformat(timespec="seconds")
        source = {
            "symbol": symbol,
            "frequency": frequency,
            "bob": bob,
            "eob": eob,
            "openPrice": number(get("open")),
            "highPrice": number(get("high")),
            "lowPrice": number(get("low")),
            "closePrice": number(get("close")),
            "preClose": optional_number(get("pre_close", "preclose")),
            "volume": integer(get("volume")),
            "amount": number(get("amount")),
        }
        source["sourceRowHash"] = hashlib.sha256(
            json.dumps(
                source,
                ensure_ascii=False,
                sort_keys=True,
                default=str,
                separators=(",", ":"),
            ).encode("utf-8")
        ).hexdigest()
        return source


@dataclass(frozen=True)
class CollectionJob:
    cycle_id: str
    windows: tuple[dict[str, str], ...]
    symbols: tuple[str, ...]


@dataclass(frozen=True)
class SparseBarManifest:
    symbol: str
    frequency: str
    missing_eobs: tuple[str, ...]
    confirmations: int = SPARSE_CONFIRMATIONS_REQUIRED

    def as_payload(self) -> dict[str, Any]:
        return {
            "symbol": self.symbol,
            "frequency": self.frequency,
            "missingEobs": list(self.missing_eobs),
            "confirmations": self.confirmations,
        }


@dataclass(frozen=True)
class CollectionOutcome:
    pid: int
    succeeded_symbols: tuple[str, ...]
    failures: dict[str, str]
    pushed_bars: int
    sparse_manifest: tuple[SparseBarManifest, ...] = ()


@dataclass
class ActiveJob:
    worker_id: int
    symbols: tuple[str, ...]
    started_at: float
    last_pid: int | None = None


class CollectorStateStore:
    """Durable consecutive-failure and local blacklist state (contains no secret)."""

    def __init__(self, directory: Path) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        self._path = directory / "collector-state.json"
        self._data: dict[str, Any] = {"failures": {}, "blacklist": {}}
        if self._path.exists():
            try:
                loaded = json.loads(self._path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict):
                    self._data.update(loaded)
            except (OSError, ValueError) as error:
                raise CollectorError(f"无法读取采集状态文件 {self._path}: {error}") from error
        self.prune()

    def failure_count(self, symbol: str) -> int:
        item = self._data["failures"].get(symbol.upper(), {})
        return int(item.get("count", 0))

    def apply_results(
        self,
        succeeded_symbols: Iterable[str],
        failures: dict[str, str],
    ) -> tuple[dict[str, int], dict[str, datetime]]:
        """Atomically persist all state changes from one completed worker job."""
        changed = False
        for symbol in succeeded_symbols:
            key = symbol.upper()
            if key in self._data["failures"]:
                del self._data["failures"][key]
                changed = True

        failure_counts: dict[str, int] = {}
        newly_blacklisted: dict[str, datetime] = {}
        updated_at = utc_now_text()
        now = datetime.now(timezone.utc)
        for symbol, error in failures.items():
            key = symbol.upper()
            count = self.failure_count(key) + 1
            failure_counts[key] = count
            sanitized = sanitize_error(error)
            if count >= MAX_FAILURE_ATTEMPTS:
                until = now + BLACKLIST_DURATION
                self._data["blacklist"][key] = {
                    "until": until.isoformat(),
                    "reason": sanitized,
                }
                self._data["failures"].pop(key, None)
                newly_blacklisted[key] = until
            else:
                self._data["failures"][key] = {
                    "count": count,
                    "lastError": sanitized,
                    "updatedAt": updated_at,
                }
            changed = True

        if changed:
            self._save()
        return failure_counts, newly_blacklisted

    def record_failure(self, symbol: str, error: str) -> int:
        key = symbol.upper()
        count = self.failure_count(key) + 1
        self._data["failures"][key] = {
            "count": count,
            "lastError": sanitize_error(error),
            "updatedAt": utc_now_text(),
        }
        self._save()
        return count

    def record_success(self, symbol: str) -> None:
        key = symbol.upper()
        if key in self._data["failures"]:
            del self._data["failures"][key]
            self._save()

    def blacklist(self, symbol: str, reason: str) -> datetime:
        key = symbol.upper()
        until = datetime.now(timezone.utc) + BLACKLIST_DURATION
        self._data["blacklist"][key] = {
            "until": until.isoformat(),
            "reason": sanitize_error(reason),
        }
        self._data["failures"].pop(key, None)
        self._save()
        return until

    def active_blacklist(self) -> dict[str, dict[str, Any]]:
        self.prune()
        return dict(self._data["blacklist"])

    def prune(self) -> None:
        now = datetime.now(timezone.utc)
        expired: list[str] = []
        for symbol, item in self._data.get("blacklist", {}).items():
            try:
                until = datetime.fromisoformat(str(item["until"]))
                if until.tzinfo is None:
                    until = until.replace(tzinfo=timezone.utc)
                if until <= now:
                    expired.append(symbol)
            except (KeyError, TypeError, ValueError):
                expired.append(symbol)
        for symbol in expired:
            self._data["blacklist"].pop(symbol, None)
        if expired:
            self._save()

    def _save(self) -> None:
        temporary = self._path.with_suffix(".tmp")
        try:
            temporary.write_text(
                json.dumps(self._data, ensure_ascii=False, indent=2), encoding="utf-8"
            )
        except OSError as error:
            raise CollectorFatalError(
                f"无法写入采集状态临时文件 {temporary}: {error}"
            ) from error

        for attempt in range(len(STATE_REPLACE_RETRY_DELAYS_SECONDS) + 1):
            try:
                os.replace(temporary, self._path)
                return
            except OSError as error:
                retryable = (
                    isinstance(error, PermissionError)
                    or getattr(error, "winerror", None) == 5
                )
                if (
                    not retryable
                    or attempt >= len(STATE_REPLACE_RETRY_DELAYS_SECONDS)
                ):
                    raise CollectorFatalError(
                        f"无法原子替换采集状态文件 {self._path}: {error}"
                    ) from error
                delay = STATE_REPLACE_RETRY_DELAYS_SECONDS[attempt]
                LOGGER.warning(
                    "state replace temporarily denied; retrying in %.2fs (%s/%s): %s",
                    delay,
                    attempt + 1,
                    len(STATE_REPLACE_RETRY_DELAYS_SECONDS),
                    error,
                )
                time.sleep(delay)


def number(value: Any) -> float:
    if value is None:
        raise CollectorError("SDK 返回了缺失价格/成交额字段的 K 线。")
    try:
        return float(value)
    except (TypeError, ValueError) as error:
        raise CollectorError(f"SDK 返回了无法转换的数值: {value}") from error


def optional_number(value: Any) -> float | None:
    return None if value is None else number(value)


def normalize_symbol(value: Any) -> str:
    return str(value or "").strip().upper()


def is_strict_a_share(value: Any) -> bool:
    return STRICT_A_SHARE_PATTERN.fullmatch(normalize_symbol(value)) is not None


def is_st_security_name(value: Any) -> bool:
    normalized = str(value or "").strip().upper().replace("＊", "*")
    return ST_SECURITY_NAME_PATTERN.match(normalized) is not None


def optional_date(value: Any) -> date | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text or text.lower() in {"none", "nat", "nan", "null"}:
        return None
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    to_python = getattr(value, "to_pydatetime", None)
    if callable(to_python):
        converted = to_python()
        if isinstance(converted, datetime):
            return converted.date()
        if isinstance(converted, date):
            return converted
    try:
        return date.fromisoformat(text[:10])
    except ValueError as error:
        raise CollectorError(f"gm 返回了无法解析的日期: {value}") from error


def strict_bool(value: Any, field: str) -> bool:
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if text in {"1", "1.0", "true"}:
        return True
    if text in {"0", "0.0", "false"}:
        return False
    raise CollectorError(f"gm 返回了无效布尔字段 {field}: {value}")


def universe_payload_hash(symbols: list[dict[str, Any]]) -> str:
    encoded = json.dumps(
        symbols,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def integer(value: Any) -> int:
    if value is None:
        raise CollectorError("SDK 返回了缺失成交量字段的 K 线。")
    try:
        return int(float(value))
    except (TypeError, ValueError) as error:
        raise CollectorError(f"SDK 返回了无法转换的成交量: {value}") from error


def iso_datetime(value: Any) -> str:
    if isinstance(value, datetime):
        return value.replace(tzinfo=None).isoformat(timespec="seconds")
    text = str(value).strip().replace("Z", "+00:00")
    try:
        return (
            datetime.fromisoformat(text)
            .replace(tzinfo=None)
            .isoformat(timespec="seconds")
        )
    except ValueError as error:
        raise CollectorError(f"无法解析 SDK K 线时间: {value}") from error


def chunks(values: list[str], size: int) -> Iterable[list[str]]:
    for index in range(0, len(values), size):
        yield values[index : index + size]


def parse_plan_time(value: Any) -> datetime:
    return datetime.fromisoformat(str(value).replace("Z", "+00:00")).replace(
        tzinfo=None
    )


def planned_eobs(
    frequency: str,
    start: datetime,
    end: datetime,
) -> set[datetime]:
    """Return the exact exchange-session EOB set for one API plan window."""
    if start.date() != end.date() or start >= end:
        raise CollectorError(
            f"计划窗口日期/顺序无效: {frequency} {start.isoformat()}/{end.isoformat()}"
        )
    normalized = frequency.lower()
    closes: list[tuple[int, int]]
    if normalized == "5m":
        closes = []
        value = datetime.combine(start.date(), datetime.min.time()).replace(
            hour=9, minute=35
        )
        morning_end = value.replace(hour=11, minute=30)
        while value <= morning_end:
            closes.append((value.hour, value.minute))
            value += timedelta(minutes=5)
        value = value.replace(hour=13, minute=5)
        afternoon_end = value.replace(hour=15, minute=0)
        while value <= afternoon_end:
            closes.append((value.hour, value.minute))
            value += timedelta(minutes=5)
    elif normalized == "30m":
        closes = [
            (10, 0), (10, 30), (11, 0), (11, 30),
            (13, 30), (14, 0), (14, 30), (15, 0),
        ]
    elif normalized == "60m":
        closes = [(10, 30), (11, 30), (14, 0), (15, 0)]
    elif normalized == "1d":
        closes = [(15, 0)]
    else:
        raise CollectorError(f"计划包含不支持的周期: {frequency}")
    result = {
        datetime.combine(start.date(), datetime.min.time()).replace(
            hour=hour, minute=minute
        )
        for hour, minute in closes
        if start
        < datetime.combine(start.date(), datetime.min.time()).replace(
            hour=hour, minute=minute
        )
        <= end
    }
    if not result:
        raise CollectorError(
            f"计划窗口没有合法闭合 EOB: {frequency} {start.isoformat()}/{end.isoformat()}"
        )
    return result


def _warm_worker(delay_seconds: float) -> int:
    time.sleep(delay_seconds)
    return os.getpid()


_SPARSE_COMPARISON_FIELDS = (
    "symbol",
    "frequency",
    "bob",
    "eob",
    "openPrice",
    "highPrice",
    "lowPrice",
    "closePrice",
    "preClose",
    "volume",
    "amount",
    "sourceRowHash",
)


def sparse_bar_map_signature(
    rows_by_eob: dict[datetime, dict[str, Any]],
) -> tuple[tuple[str, str], ...]:
    """Canonical, order-independent proof over every material official field."""
    signature: list[tuple[str, str]] = []
    for eob, row in sorted(rows_by_eob.items()):
        missing_fields = [field for field in _SPARSE_COMPARISON_FIELDS if field not in row]
        if missing_fields:
            raise CollectorError(
                f"稀疏复核行缺少字段: {','.join(missing_fields)}"
            )
        material = {field: row[field] for field in _SPARSE_COMPARISON_FIELDS}
        signature.append(
            (
                eob.isoformat(timespec="seconds"),
                json.dumps(
                    material,
                    ensure_ascii=False,
                    sort_keys=True,
                    default=str,
                    separators=(",", ":"),
                ),
            )
        )
    return tuple(signature)


def collect_partition(settings: Settings, job: CollectionJob) -> CollectionOutcome:
    """Worker entry point; fetch/push one partition and return per-symbol results."""
    if len(job.symbols) > SYMBOLS_PER_JOB:
        raise CollectorError(
            f"worker job 超过 {SYMBOLS_PER_JOB} 只股票: {len(job.symbols)}"
        )
    if settings.provider != "gm":
        raise CollectorError(f"不支持的数据源 provider: {settings.provider}")

    client = ApiClient(settings)
    provider = GmHistoryProvider(settings.gm_token)
    expected_eobs_by_frequency: dict[str, set[datetime]] = {}
    legal_session_eobs_by_frequency: dict[str, set[datetime]] = {}
    for window in job.windows:
        frequency = str(window["frequency"]).lower()
        if frequency in expected_eobs_by_frequency:
            raise CollectorError(f"计划重复包含周期 {frequency}")
        start = parse_plan_time(window["from"])
        end = parse_plan_time(window["to"])
        expected_eobs_by_frequency[frequency] = planned_eobs(frequency, start, end)
        session_start = datetime.combine(start.date(), datetime.min.time())
        session_end = session_start.replace(
            hour=23, minute=59, second=59, microsecond=999999
        )
        legal_session_eobs_by_frequency[frequency] = planned_eobs(
            frequency, session_start, session_end
        )
    received_eobs: dict[str, dict[str, set[datetime]]] = {
        symbol: {frequency: set() for frequency in expected_eobs_by_frequency}
        for symbol in job.symbols
    }
    rows_by_symbol: dict[str, list[dict[str, Any]]] = {
        symbol: [] for symbol in job.symbols
    }
    verified_sparse: dict[tuple[str, str], SparseBarManifest] = {}
    errors: dict[str, list[str]] = {}
    pushed = 0

    def validate_response(
        rows: list[dict[str, Any]],
        requested_symbols: list[str],
        frequency: str,
        window_start: datetime,
    ) -> tuple[
        dict[str, dict[datetime, dict[str, Any]]],
        dict[str, list[str]],
    ]:
        requested = set(requested_symbols)
        result: dict[str, dict[datetime, dict[str, Any]]] = {
            symbol: {} for symbol in requested_symbols
        }
        issues: dict[str, list[str]] = {}
        returned_symbols = {
            str(row.get("symbol", "")).strip().upper() for row in rows
        }
        unexpected = sorted(returned_symbols - requested)
        if unexpected:
            detail = f"{frequency}:SDK返回批次外证券:{unexpected[:10]}"
            return result, {symbol: [detail] for symbol in requested_symbols}

        expected_eobs = expected_eobs_by_frequency[frequency]
        for row in rows:
            symbol = str(row.get("symbol", "")).strip().upper()
            if not symbol:
                detail = f"{frequency}:SDK返回无证券代码行"
                for requested_symbol in requested_symbols:
                    issues.setdefault(requested_symbol, []).append(detail)
                continue
            row_frequency = str(row.get("frequency", "")).strip().lower()
            if row_frequency != frequency:
                issues.setdefault(symbol, []).append(
                    f"{frequency}:返回周期不一致:{row_frequency or '<empty>'}"
                )
                continue
            try:
                eob = parse_plan_time(row.get("eob"))
            except Exception as error:
                issues.setdefault(symbol, []).append(
                    f"{frequency}:EOB无法解析:{type(error).__name__}:{error}"
                )
                continue
            # gm history uses an inclusive lower bound and can repeat already
            # committed bar exactly at the plan's exclusive `from` watermark.
            # Ignore only a valid exchange-session EOB for this exact day and
            # frequency. It is not returned, pushed, counted, or used as sparse
            # proof. Future, in-window-extra, cross-day and illegal EOBs remain
            # strict failures.
            if (
                eob == window_start
                and eob in legal_session_eobs_by_frequency[frequency]
            ):
                continue
            if eob not in expected_eobs:
                issues.setdefault(symbol, []).append(
                    f"{frequency}:非计划EOB:{eob.isoformat(timespec='seconds')}"
                )
                continue
            if eob in result[symbol]:
                issues.setdefault(symbol, []).append(
                    f"{frequency}:重复EOB:{eob.isoformat(timespec='seconds')}"
                )
                continue
            result[symbol][eob] = row
        return result, issues

    for window in job.windows:
        frequency = str(window["frequency"]).lower()
        start = parse_plan_time(window["from"])
        end = parse_plan_time(window["to"])
        for sdk_group in chunks(list(job.symbols), settings.symbols_per_sdk_request):
            try:
                rows = provider.fetch(sdk_group, frequency, start, end)
            except ProviderUnavailableError:
                raise
            except Exception as error:
                detail = f"{frequency}:{type(error).__name__}:{error}"
                for symbol in sdk_group:
                    errors.setdefault(symbol, []).append(detail)
                continue

            first_maps, first_issues = validate_response(
                rows, sdk_group, frequency, start
            )
            for symbol, symbol_issues in first_issues.items():
                errors.setdefault(symbol, []).extend(symbol_issues)

            if (
                sum(len(bar_map) for bar_map in first_maps.values()) == 0
                and not first_issues
            ):
                detail = (
                    f"{frequency}:初始SDK分组在整个计划窗口没有任何有效计划行，"
                    "判定为供应商频率/窗口暂不可用；本轮中止且不累计个股失败"
                )
                raise ProviderFrequencyUnavailableError(detail)

            expected_eobs = expected_eobs_by_frequency[frequency]
            for symbol in sdk_group:
                first_map = first_maps[symbol]
                received_eobs[symbol][frequency] = set(first_map)
                if symbol in first_issues:
                    continue
                if set(first_map) == expected_eobs:
                    rows_by_symbol[symbol].extend(first_map.values())
                    continue

                # An absent exchange EOB is not synthesized. It is accepted only
                # after two additional, independent single-symbol reads return the
                # exact same complete material-bar mapping as the original response.
                try:
                    signatures = [sparse_bar_map_signature(first_map)]
                    probe_maps: list[dict[datetime, dict[str, Any]]] = []
                    for _ in range(SPARSE_CONFIRMATIONS_REQUIRED - 1):
                        probe_rows = provider.fetch([symbol], frequency, start, end)
                        probe_by_symbol, probe_issues = validate_response(
                            probe_rows, [symbol], frequency, start
                        )
                        if probe_issues:
                            raise CollectorError(" | ".join(probe_issues[symbol]))
                        probe_map = probe_by_symbol[symbol]
                        probe_maps.append(probe_map)
                        signatures.append(sparse_bar_map_signature(probe_map))
                    if any(signature != signatures[0] for signature in signatures[1:]):
                        counts = [len(signature) for signature in signatures]
                        raise CollectorError(
                            f"三次实际bar映射不一致 counts={counts}"
                        )
                except ProviderUnavailableError:
                    raise
                except Exception as error:
                    errors.setdefault(symbol, []).append(
                        f"{frequency}:稀疏复核失败:{type(error).__name__}:{error}"
                    )
                    continue

                missing = sorted(expected_eobs - set(first_map))
                manifest = SparseBarManifest(
                    symbol,
                    frequency,
                    tuple(value.isoformat(timespec="seconds") for value in missing),
                )
                verified_sparse[(symbol, frequency)] = manifest
                rows_by_symbol[symbol].extend(first_map.values())
                LOGGER.info(
                    "verified official sparse bars: cycle=%s symbol=%s frequency=%s "
                    "missingEobs=%s confirmations=%s",
                    job.cycle_id,
                    symbol,
                    frequency,
                    ",".join(manifest.missing_eobs),
                    manifest.confirmations,
                )

    succeeded: list[str] = []
    failures: dict[str, str] = {}
    for symbol in job.symbols:
        for frequency, expected_eobs in expected_eobs_by_frequency.items():
            actual_eobs = received_eobs[symbol][frequency]
            if (
                actual_eobs != expected_eobs
                and (symbol, frequency) not in verified_sparse
            ):
                missing = sorted(expected_eobs - actual_eobs)
                extra = sorted(actual_eobs - expected_eobs)
                errors.setdefault(symbol, []).append(
                    f"{frequency}:EOB不完整 expected={len(expected_eobs)} "
                    f"actual={len(actual_eobs)} missing="
                    f"{[value.isoformat(timespec='seconds') for value in missing[:10]]} "
                    f"extra={[value.isoformat(timespec='seconds') for value in extra[:10]]}"
                )
        if symbol in errors:
            failures[symbol] = " | ".join(errors[symbol])[:1000]
        else:
            succeeded.append(symbol)

    # Never write rows for a symbol that will be retried: this keeps retry
    # attempts free of same-cycle duplicate EOBs. A transport failure after a
    # partial push is fatal to the whole cycle and is not converted to a symbol
    # retry; the API cycle will be aborted and restarted explicitly.
    bars_to_push = [
        row
        for symbol in succeeded
        for row in rows_by_symbol[symbol]
    ]
    try:
        for offset in range(0, len(bars_to_push), settings.max_push_bars):
            batch = bars_to_push[offset : offset + settings.max_push_bars]
            client.push_batch(job.cycle_id, batch)
            pushed += len(batch)
    except Exception as error:
        raise CollectorError(
            f"WebAPI batch push failed after {pushed} rows: "
            f"{type(error).__name__}:{error}"
        ) from error

    success_set = set(succeeded)
    sparse_manifest = tuple(
        manifest
        for (symbol, _), manifest in sorted(verified_sparse.items())
        if symbol in success_set
    )
    return CollectionOutcome(
        os.getpid(), tuple(succeeded), failures, pushed, sparse_manifest
    )


class CollectorSupervisor:
    def __init__(
        self,
        settings: Settings,
        state: CollectorStateStore,
        executor: ProcessPoolExecutor,
        worker_pids: set[int],
        target_date: date | None = None,
    ) -> None:
        self.settings = settings
        self.state = state
        self.executor = executor
        self.client = ApiClient(settings)
        self.universe_provider = GmHistoryProvider(settings.gm_token)
        self.instance_id = uuid.uuid4().hex
        self.started_at = utc_now_text()
        self.worker_pids = worker_pids
        self.target_date = target_date
        self.cycles_completed = 0
        self.last_error: str | None = None
        self.current_cycle_id: str | None = None
        self._universe_synced_date: date | None = None
        self._universe_synced_at_monotonic: float | None = None
        self._provider_authorized_at_monotonic: float | None = None
        self._last_heartbeat = 0.0

    def run_once(self) -> bool:
        self._refresh_worker_pids()
        if len(self.worker_pids) != WORKER_PROCESS_COUNT:
            raise CollectorFatalError(
                f"采集进程池只剩 {len(self.worker_pids)}/{WORKER_PROCESS_COUNT} 个存活 Worker，"
                "拒绝降级运行并请求外部重启。"
            )
        today = china_today()
        trading_date = self.target_date or today
        if trading_date > today:
            raise CollectorError(
                f"采集日期 {trading_date} 晚于中国当前日期 {today}，拒绝执行。"
            )
        self._ensure_provider_authorized()
        self._ensure_universe(trading_date)
        self._require_six_workers()
        plan = self.client.get_plan(trading_date)
        if not plan.get("shouldCollect"):
            self.current_cycle_id = None
            self._heartbeat("idle", force=True)
            reason = str(plan.get("reason", "unknown"))
            LOGGER.info("no collection: %s", reason)
            if self.target_date is not None:
                raise CollectorError(f"历史补算未取得可执行计划: {reason}")
            return False

        cycle_id = str(plan["cycleId"])
        response_date = str(plan.get("tradingDate", ""))[:10]
        if response_date != trading_date.isoformat():
            self._abort_safely(
                cycle_id,
                f"API 返回交易日 {response_date}，与请求 {trading_date.isoformat()} 不一致。",
            )
            raise CollectorError("API 计划交易日不一致，已拒绝执行。")
        symbols = [str(item["symbol"]).strip().upper() for item in plan["symbols"]]
        if len(symbols) != int(plan.get("expectedSymbolCount", len(symbols))):
            self._abort_safely(cycle_id, "API 计划股票数量与 expectedSymbolCount 不一致。")
            raise CollectorError("API 计划股票数量不一致，已拒绝执行。")

        self.current_cycle_id = cycle_id
        is_historical_backfill = trading_date < today
        local_blacklist = {} if is_historical_backfill else self.state.active_blacklist()
        blocked = [symbol for symbol in symbols if symbol in local_blacklist]
        if blocked:
            for symbol in blocked:
                item = local_blacklist[symbol]
                try:
                    self.client.report_blacklist(
                        self.settings.collector_id,
                        symbol,
                        str(item.get("reason", "local-blacklist")),
                        MAX_FAILURE_ATTEMPTS,
                    )
                except CollectorError as error:
                    LOGGER.error("重新上报黑名单失败 %s: %s", symbol, error)
            reason = f"计划仍包含 {len(blocked)} 只本地黑名单股票，已中止等待 API 排除。"
            self._abort_safely(cycle_id, reason)
            self.last_error = reason
            self._heartbeat("degraded", blacklisted=len(local_blacklist), force=True)
            return False

        windows = tuple(
            {
                "frequency": str(item["frequency"]),
                "from": str(item["from"]),
                "to": str(item["to"]),
            }
            for item in plan["windows"]
        )
        pending: deque[tuple[str, ...]] = deque(
            tuple(group) for group in chunks(symbols, SYMBOLS_PER_JOB)
        )
        active: dict[Future[CollectionOutcome], ActiveJob] = {}
        succeeded: set[str] = set()
        failed_symbols: dict[str, str] = {}
        blacklisted: dict[str, str] = {}
        sparse_manifest: dict[tuple[str, str], SparseBarManifest] = {}
        pushed_bars = 0
        available_worker_ids = set(range(1, WORKER_PROCESS_COUNT + 1))
        stop_scheduling = False
        submitted_for_compute = False

        try:
            while pending or active:
                self._require_six_workers()
                while pending and len(active) < WORKER_PROCESS_COUNT and not stop_scheduling:
                    group = pending.popleft()
                    job = CollectionJob(cycle_id, windows, group)
                    future = self.executor.submit(collect_partition, self.settings, job)
                    worker_id = min(available_worker_ids)
                    available_worker_ids.remove(worker_id)
                    active[future] = ActiveJob(worker_id, group, time.monotonic())

                if not active:
                    break

                done, _ = wait(
                    active,
                    timeout=self.settings.heartbeat_seconds,
                    return_when=FIRST_COMPLETED,
                )
                self._heartbeat(
                    "retrying" if failed_symbols else "collecting",
                    active=active,
                    queued=sum(len(group) for group in pending),
                    succeeded=len(succeeded),
                    retrying=len(failed_symbols),
                    blacklisted=len(blacklisted),
                )
                for future in done:
                    active_job = active.pop(future)
                    available_worker_ids.add(active_job.worker_id)
                    try:
                        outcome = future.result()
                        self.worker_pids.add(outcome.pid)
                        if len(self.worker_pids) > WORKER_PROCESS_COUNT:
                            # Windows assigns increasing PIDs when the executor replaces
                            # a crashed worker. Retain the most recently observed six.
                            self.worker_pids = set(
                                sorted(self.worker_pids)[-WORKER_PROCESS_COUNT:]
                            )
                        pushed_bars += outcome.pushed_bars
                        outcome_failures = outcome.failures
                        outcome_successes = outcome.succeeded_symbols
                        outcome_sparse_manifest = outcome.sparse_manifest
                    except ProviderUnavailableError:
                        # An empty provider frequency/window is a shared upstream
                        # condition, not N independent symbol failures. No state
                        # mutation has happened for this outcome.
                        raise
                    except BrokenProcessPool as error:
                        raise CollectorFatalError(
                            "采集 Worker 进程池已损坏；该故障不会计入任何股票失败次数。"
                        ) from error
                    except Exception as error:
                        raise CollectorFatalError(
                            f"采集 Worker 异常退出；本轮不计入任何股票失败次数: "
                            f"{type(error).__name__}:{error}"
                        ) from error

                    self._require_six_workers()

                    failure_counts, newly_blacklisted = self.state.apply_results(
                        outcome_successes, outcome_failures
                    )
                    retry: list[str] = []
                    for symbol in outcome_successes:
                        succeeded.add(symbol)
                        failed_symbols.pop(symbol, None)
                    for item in outcome_sparse_manifest:
                        if item.symbol in outcome_successes:
                            sparse_manifest[(item.symbol, item.frequency)] = item
                    for symbol, error in outcome_failures.items():
                        state_key = symbol.upper()
                        succeeded.discard(symbol)
                        for key in [key for key in sparse_manifest if key[0] == symbol]:
                            sparse_manifest.pop(key, None)
                        failed_symbols[symbol] = error
                        failure_count = failure_counts[state_key]
                        if state_key in newly_blacklisted:
                            blacklisted[symbol] = error
                            if not is_historical_backfill:
                                try:
                                    self.client.report_blacklist(
                                        self.settings.collector_id,
                                        symbol,
                                        error,
                                        failure_count,
                                    )
                                except CollectorError as report_error:
                                    LOGGER.error("上报黑名单失败 %s: %s", symbol, report_error)
                            stop_scheduling = True
                        else:
                            retry.append(symbol)
                    if not stop_scheduling:
                        for group in chunks(retry, SYMBOLS_PER_JOB):
                            pending.append(tuple(group))

                if stop_scheduling:
                    pending.clear()

            if blacklisted:
                examples = ",".join(sorted(blacklisted)[:10])
                reason = (
                    f"{len(blacklisted)} 只股票连续失败 {MAX_FAILURE_ATTEMPTS} 次，"
                    f"已加入 24 小时黑名单；示例:{examples}"
                )
                self.last_error = reason
                self._heartbeat(
                    "degraded",
                    succeeded=len(succeeded),
                    blacklisted=len(self.state.active_blacklist()),
                    force=True,
                )
                LOGGER.error("cycle %s aborted: %s", cycle_id, reason)
                if is_historical_backfill:
                    # One-shot historical work must never report success after an
                    # aborted cycle. Let the common exception path report exactly
                    # one abort and return process exit code 1.
                    raise CollectorError(reason)
                self._abort_safely(cycle_id, reason)
                return False

            expected = set(symbols)
            if succeeded != expected:
                missing = sorted(expected - succeeded)
                reason = f"调度结束仍缺少 {len(missing)} 只股票，示例:{missing[:10]}"
                raise CollectorError(reason)

            sparse_payload = [
                item.as_payload()
                for _, item in sorted(sparse_manifest.items())
            ]
            if sparse_payload:
                LOGGER.info(
                    "cycle %s verified sparse manifest: combinations=%s missingEobs=%s "
                    "details=%s",
                    cycle_id,
                    len(sparse_payload),
                    sum(len(item["missingEobs"]) for item in sparse_payload),
                    ";".join(
                        f"{item['symbol']}/{item['frequency']}="
                        f"{','.join(item['missingEobs'])}"
                        for item in sparse_payload
                    ),
                )
            self.client.complete(cycle_id, symbols, sparse_payload)
            submitted_for_compute = True
            if is_historical_backfill:
                self._wait_for_backfill_compute(
                    cycle_id, trading_date, windows
                )
            self.cycles_completed += 1
            self.last_error = None
            self.current_cycle_id = None
            self._heartbeat(
                "idle",
                succeeded=len(succeeded),
                blacklisted=len(self.state.active_blacklist()),
                force=True,
            )
            LOGGER.info(
                "cycle %s accepted: %s bars for %s symbols",
                cycle_id,
                pushed_bars,
                len(symbols),
            )
            return True
        except ProviderUnavailableError as error:
            reason = str(error)
            self.last_error = reason
            if not submitted_for_compute:
                self._abort_safely(cycle_id, reason)
            self.current_cycle_id = None
            self._heartbeat(
                "degraded",
                blacklisted=len(self.state.active_blacklist()),
                force=True,
            )
            raise
        except BrokenProcessPool as error:
            fatal = CollectorFatalError(
                "采集 Worker 进程池已损坏；该故障不会计入任何股票失败次数。"
            )
            self.last_error = str(fatal)
            if not submitted_for_compute:
                self._abort_safely(cycle_id, str(fatal))
            self._heartbeat("failed", force=True)
            raise fatal from error
        except Exception as error:
            self.last_error = str(error)
            if not submitted_for_compute:
                self._abort_safely(cycle_id, str(error))
            self._heartbeat("failed", force=True)
            raise

    def _wait_for_backfill_compute(
        self,
        cycle_id: str,
        trading_date: date,
        windows: tuple[dict[str, str], ...],
    ) -> None:
        deadline = time.monotonic() + BACKFILL_COMPUTE_TIMEOUT_SECONDS
        expected_watermarks = {
            str(window["frequency"]).lower(): parse_plan_time(window["to"])
            for window in windows
        }
        while time.monotonic() < deadline:
            status = self.client.get_status()
            response_date = str(status.get("tradingDate", ""))[:10]
            if response_date != trading_date.isoformat():
                raise CollectorError(
                    f"等待计算时 API session 日期变为 {response_date}，"
                    f"预期 {trading_date.isoformat()}。"
                )
            state = str(status.get("status", "")).lower()
            last_error = str(status.get("lastError") or "").strip()
            if state == "failed" or last_error:
                raise CollectorError(
                    f"WebAPI 历史对子计算失败: {last_error or state}"
                )
            if state == "idle" and status.get("lastCompletedAt"):
                actual_watermarks = {
                    str(frequency).lower(): parse_plan_time(value)
                    for frequency, value in dict(status.get("watermarks") or {}).items()
                }
                if actual_watermarks == expected_watermarks:
                    LOGGER.info(
                        "historical pair computation completed: date=%s cycle=%s watermarks=%s",
                        trading_date,
                        cycle_id,
                        ",".join(
                            f"{frequency}:{value.isoformat(timespec='seconds')}"
                            for frequency, value in sorted(actual_watermarks.items())
                        ),
                    )
                    return
            self._heartbeat("computing")
            time.sleep(5)
        raise CollectorError(
            f"WebAPI 历史对子计算等待超过 {BACKFILL_COMPUTE_TIMEOUT_SECONDS} 秒；"
            "cycle 已提交，未发送 abort，请从 API 状态和日志继续核查。"
        )

    def _abort_safely(self, cycle_id: str, error: str) -> None:
        try:
            self.client.abort(cycle_id, error)
        except CollectorError as abort_error:
            LOGGER.error("cycle %s abort report failed: %s", cycle_id, abort_error)

    def _ensure_universe(self, trading_date: date) -> None:
        now_monotonic = time.monotonic()
        if (
            self._universe_synced_date == trading_date
            and self._universe_synced_at_monotonic is not None
            and now_monotonic - self._universe_synced_at_monotonic
            < self.settings.universe_refresh_seconds
        ):
            return
        self._heartbeat("syncing", force=True)
        try:
            snapshot = self.universe_provider.fetch_authoritative_universe(trading_date)
            source = (
                "dongcai-gm-history"
                if trading_date < china_today()
                else "dongcai-gm"
            )
            payload = {
                "collectorId": self.settings.collector_id,
                "tradingDate": snapshot.trading_date.isoformat(),
                "isTradingDay": snapshot.is_trading_day,
                "source": source,
                "sourceUpdatedAt": snapshot.source_updated_at.isoformat(),
                "symbols": list(snapshot.symbols),
            }
            response = self.client.synchronize_universe(payload)
            status = str(response.get("status", "")).lower()
            response_date = str(response.get("tradingDate", ""))[:10]
            response_trading_day = response.get("isTradingDay")
            expected_count = len(snapshot.symbols)
            expected_eligible = sum(
                1
                for item in snapshot.symbols
                if not item["isSt"] and not item["isSuspended"]
            )
            actual_count = int(response.get("totalSymbols", -1))
            actual_eligible = int(response.get("eligibleSymbols", -1))
            universe_version = str(response.get("universeVersion", "")).strip()
            payload_hash = str(response.get("payloadHash", "")).strip()
            if (
                status != "completed"
                or response_date != trading_date.isoformat()
                or response_trading_day is not snapshot.is_trading_day
                or actual_count != expected_count
                or actual_eligible != expected_eligible
                or not universe_version
                or not payload_hash
            ):
                raise CollectorError(
                    "WebAPI 未确认请求交易日权威股票池完整同步: "
                    f"status={status},date={response_date},"
                    f"isTradingDay={response_trading_day},"
                    f"symbols={actual_count}/{expected_count},"
                    f"eligible={actual_eligible}/{expected_eligible}"
                )
            self._universe_synced_date = trading_date
            self._universe_synced_at_monotonic = time.monotonic()
            self.last_error = None
            LOGGER.info(
                "authoritative universe synced: date=%s tradingDay=%s total=%s eligible=%s version=%s",
                trading_date,
                snapshot.is_trading_day,
                expected_count,
                actual_eligible,
                universe_version,
            )
        except Exception as error:
            self.last_error = f"请求交易日权威股票池同步失败: {error}"
            self._heartbeat(
                "degraded" if isinstance(error, ProviderUnavailableError) else "failed",
                force=True,
            )
            if isinstance(error, CollectorError):
                raise
            raise CollectorError(self.last_error) from error

    def _ensure_provider_authorized(self) -> None:
        now = time.monotonic()
        if (
            self._provider_authorized_at_monotonic is not None
            and now - self._provider_authorized_at_monotonic
            < PROVIDER_AUTHORIZATION_REFRESH_SECONDS
        ):
            return
        self._heartbeat("validating_provider", force=True)
        try:
            self.universe_provider.validate_authorized_history()
        except ProviderUnavailableError as error:
            self.last_error = str(error)
            self._heartbeat("degraded", force=True)
            raise
        self._provider_authorized_at_monotonic = time.monotonic()
        self.last_error = None
        LOGGER.info("gm history authorization preflight passed")

    def _heartbeat(
        self,
        status: str,
        *,
        active: dict[Future[CollectionOutcome], ActiveJob] | None = None,
        queued: int = 0,
        succeeded: int = 0,
        retrying: int = 0,
        blacklisted: int | None = None,
        force: bool = False,
    ) -> None:
        now = time.monotonic()
        if not force and now - self._last_heartbeat < self.settings.heartbeat_seconds:
            return
        self._refresh_worker_pids()
        active = active or {}
        pid_by_worker = {
            worker_id: pid
            for worker_id, pid in enumerate(sorted(self.worker_pids), start=1)
        }
        workers: list[dict[str, Any]] = []
        for future, job in active.items():
            workers.append(
                {
                    "workerId": job.worker_id,
                    "pid": pid_by_worker.get(job.worker_id),
                    "state": "running" if not future.done() else "completed",
                    "assignedSymbols": len(job.symbols),
                    "completedSymbols": 0,
                    "failedSymbols": 0,
                    "currentSymbol": job.symbols[0] if job.symbols else None,
                    "lastError": None,
                }
            )
        busy_ids = {item["workerId"] for item in workers}
        for worker_id in range(1, WORKER_PROCESS_COUNT + 1):
            if worker_id not in busy_ids:
                workers.append(
                    {
                        "workerId": worker_id,
                        "pid": pid_by_worker.get(worker_id),
                        "state": "idle",
                        "assignedSymbols": 0,
                        "completedSymbols": 0,
                        "failedSymbols": 0,
                        "currentSymbol": None,
                        "lastError": None,
                    }
                )
        payload = {
            "collectorId": self.settings.collector_id,
            "instanceId": self.instance_id,
            "status": status,
            "processLimit": WORKER_PROCESS_COUNT,
            "processesExpected": WORKER_PROCESS_COUNT,
            "processesRunning": len(self.worker_pids),
            "activeProcesses": len(self.worker_pids),
            "activeJobs": len(active),
            "queuedJobs": (queued + SYMBOLS_PER_JOB - 1) // SYMBOLS_PER_JOB,
            "queuedSymbols": queued,
            "succeededSymbols": succeeded,
            "retryingJobs": retrying,
            "failedSymbols": retrying,
            "blacklistedSymbols": blacklisted
            if blacklisted is not None
            else len(self.state.active_blacklist()),
            "cyclesCompleted": self.cycles_completed,
            "currentCycleId": self.current_cycle_id,
            "hostName": socket.gethostname(),
            "version": COLLECTOR_VERSION,
            "startedAt": self.started_at,
            "lastError": sanitize_error(self.last_error),
            "workers": sorted(workers, key=lambda item: item["workerId"]),
            "workerPids": sorted(self.worker_pids),
        }
        try:
            self.client.heartbeat(payload)
            self._last_heartbeat = time.monotonic()
        except CollectorError as error:
            LOGGER.warning("heartbeat failed: %s", error)

    def wait_for_provider_retry(self, seconds: int) -> None:
        """Back off provider retries without making a live supervisor look offline."""
        deadline = time.monotonic() + max(0, seconds)
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            time.sleep(min(self.settings.heartbeat_seconds, remaining))
            self._heartbeat(
                "degraded",
                blacklisted=len(self.state.active_blacklist()),
                force=True,
            )

    def _refresh_worker_pids(self) -> None:
        """Report live executor children, including replacements after a crash."""
        processes = getattr(self.executor, "_processes", None)
        if not isinstance(processes, dict):
            return
        live = {
            process.pid
            for process in processes.values()
            if process.pid is not None and process.is_alive()
        }
        self.worker_pids = live

    def _require_six_workers(self) -> None:
        self._refresh_worker_pids()
        if len(self.worker_pids) != WORKER_PROCESS_COUNT:
            raise CollectorFatalError(
                f"采集 Worker 存活数为 {len(self.worker_pids)}/{WORKER_PROCESS_COUNT}；"
                "本轮不计入股票失败并退出等待计划任务重启。"
            )


def utc_now_text() -> str:
    return datetime.now(timezone.utc).isoformat()


def china_today() -> date:
    """Current calendar date in Asia/Shanghai; evaluated for every supervisor loop."""
    return datetime.now(CHINA_TIMEZONE).date()


def sanitize_error(value: str | None) -> str | None:
    if not value:
        return None
    return SECRET_PATTERN.sub(r"\1=***", str(value))[:1000]


def configure_logging(state_directory: Path) -> None:
    state_directory.mkdir(parents=True, exist_ok=True)
    LOGGER.setLevel(logging.INFO)
    formatter = logging.Formatter(
        "%(asctime)s %(levelname)s pid=%(process)d %(message)s"
    )
    stream = logging.StreamHandler()
    stream.setFormatter(formatter)
    file_handler = RotatingFileHandler(
        state_directory / "collector.log",
        maxBytes=10 * 1024 * 1024,
        backupCount=5,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    LOGGER.handlers.clear()
    LOGGER.addHandler(stream)
    LOGGER.addHandler(file_handler)


def warm_pool(executor: ProcessPoolExecutor) -> set[int]:
    futures = [executor.submit(_warm_worker, 0.5) for _ in range(WORKER_PROCESS_COUNT)]
    pids = {future.result() for future in futures}
    if len(pids) != WORKER_PROCESS_COUNT:
        raise CollectorError(
            f"进程池预热只启动了 {len(pids)}/{WORKER_PROCESS_COUNT} 个 worker，"
            "拒绝以降级并发运行。"
        )
    return pids


def validate_config(settings: Settings) -> None:
    """Safe deployment smoke check: no plan, batch, complete, or watermark mutation."""
    if settings.provider != "gm":
        raise CollectorError(f"不支持的数据源 provider: {settings.provider}")
    ApiClient(settings).get_status()
    provider = GmHistoryProvider(settings.gm_token)
    provider.validate()
    provider.validate_authorized_history()
    LOGGER.info(
        "configuration valid: WebAPI authentication and gm history authorization passed"
    )


def settings_for_run(
    settings: Settings,
    backfill_date: date | None,
) -> Settings:
    """Give historical full-universe transactions a bounded longer HTTP window."""
    if backfill_date is None or settings.request_timeout_seconds >= 180:
        return settings
    return replace(settings, request_timeout_seconds=180)


def run_supervisor_loop(
    supervisor: CollectorSupervisor,
    *,
    once: bool,
    poll_seconds: int,
) -> int:
    """Run collection and preserve failure semantics for one-shot operations."""
    while True:
        next_poll_seconds = poll_seconds
        provider_unavailable = False
        try:
            supervisor.run_once()
        except CollectorFatalError as error:
            LOGGER.critical("collector infrastructure failed; restart required: %s", error)
            return 1
        except CollectorError as error:
            LOGGER.error("collection failed: %s", error)
            if once:
                return 1
            if isinstance(error, ProviderUnavailableError):
                provider_unavailable = True
                next_poll_seconds = max(
                    poll_seconds, PROVIDER_FREQUENCY_RETRY_SECONDS
                )
                LOGGER.warning(
                    "provider unavailable; retrying in %ss without "
                    "counting symbol failures",
                    next_poll_seconds,
                )
        if once:
            return 0
        if provider_unavailable:
            supervisor.wait_for_provider_retry(next_poll_seconds)
        else:
            time.sleep(next_poll_seconds)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.local.json")
    parser.add_argument("--once", action="store_true")
    parser.add_argument(
        "--backfill-date",
        type=date.fromisoformat,
        help="严格补采一个过去交易日（YYYY-MM-DD）；必须与 --once 同时使用。",
    )
    parser.add_argument(
        "--validate-config",
        "--check-config",
        dest="validate_config",
        action="store_true",
        help="只验证配置、WebAPI 鉴权和 gm SDK 初始化，不领取采集计划。",
    )
    args = parser.parse_args()
    if args.backfill_date is not None:
        if not args.once:
            parser.error("--backfill-date 必须与 --once 同时使用，禁止历史任务常驻轮询。")
        if args.backfill_date >= china_today():
            parser.error("--backfill-date 只接受早于中国当前日期的历史交易日。")
    config_path = Path(args.config).resolve()
    try:
        settings = settings_for_run(Settings.load(config_path), args.backfill_date)
        state_directory = Path(settings.state_directory)
        configure_logging(state_directory)
        state_path = (
            state_directory / "backfill" / args.backfill_date.isoformat()
            if args.backfill_date is not None
            else state_directory
        )
        # 历史补算使用独立失败状态，不能把历史供应商缺口污染成当日采集黑名单。
        state = CollectorStateStore(state_path)
        if args.validate_config:
            validate_config(settings)
            return 0
        with ProcessPoolExecutor(max_workers=WORKER_PROCESS_COUNT) as executor:
            worker_pids = warm_pool(executor)
            LOGGER.info(
                "collector started: supervisor=%s workers=%s collectorId=%s",
                os.getpid(),
                sorted(worker_pids),
                settings.collector_id,
            )
            supervisor = CollectorSupervisor(
                settings, state, executor, worker_pids, args.backfill_date
            )
            return run_supervisor_loop(
                supervisor, once=args.once, poll_seconds=settings.poll_seconds
            )
    except KeyboardInterrupt:
        LOGGER.info("collector stopped by operator")
        return 0
    except (CollectorError, OSError, ValueError, KeyError) as error:
        if not LOGGER.handlers:
            print(f"collector startup failed: {error}", file=sys.stderr)
        else:
            LOGGER.exception("collector stopped: %s", error)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
