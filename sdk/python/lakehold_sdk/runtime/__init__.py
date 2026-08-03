"""Supported reliability helpers layered over the generated LakeHold v1 client."""

from __future__ import annotations

from collections.abc import Callable, Generator, Iterable, Iterator, Mapping
from concurrent.futures import CancelledError
from dataclasses import dataclass
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
import json
import math
import secrets
import time
from typing import Generic, Optional, TypeVar
from urllib.parse import quote, urlencode

from lakehold_sdk.api_client import ApiClient
from lakehold_sdk.exceptions import ApiException

SDK_VERSION = "0.1.0"
SDK_USER_AGENT = f"lakehold-sdk/{SDK_VERSION} (python)"
REQUEST_ID_HEADER = "X-Request-Id"

T = TypeVar("T")


class LakeholdApiClient(ApiClient):
    """Generated API client with a positive whole-request timeout applied by default."""

    def __init__(
        self,
        configuration=None,
        header_name=None,
        header_value=None,
        cookie=None,
        *,
        timeout: float,
    ) -> None:
        if not isinstance(timeout, (int, float)) or isinstance(timeout, bool):
            raise TypeError("timeout must be a number of seconds")
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be a positive finite number of seconds")
        super().__init__(configuration, header_name, header_value, cookie)
        self._lakehold_request_timeout = float(timeout)
        self.user_agent = SDK_USER_AGENT

    def call_api(
        self,
        method,
        url,
        header_params=None,
        body=None,
        post_params=None,
        _request_timeout=None,
    ):
        return super().call_api(
            method,
            url,
            header_params,
            body,
            post_params,
            self._lakehold_request_timeout if _request_timeout is None else _request_timeout,
        )


def configure(client: ApiClient) -> ApiClient:
    if client is None:
        raise ValueError("client is required")
    client.user_agent = SDK_USER_AGENT
    return client


def create_idempotency_key() -> str:
    return secrets.token_hex(16)


def validate_idempotency_key(value: str) -> str:
    if value is None:
        raise ValueError("idempotency key is required")
    if not 16 <= len(value) <= 128 or any(not "!" <= character <= "~" for character in value):
        raise ValueError("an idempotency key must contain 16-128 visible ASCII characters")
    return value


def request_id(headers) -> Optional[str]:
    if headers is None:
        return None
    for key, value in headers.items():
        if key.lower() == REQUEST_ID_HEADER.lower():
            return value[0] if isinstance(value, (list, tuple)) else str(value)
    return None


class LakeholdProblemError(RuntimeError):
    def __init__(
        self,
        status: int,
        code: str,
        request_id_value: Optional[str],
        detail: Optional[str],
        retry_after: Optional[float],
        cause: ApiException,
    ) -> None:
        super().__init__(detail or code)
        self.status = status
        self.code = code
        self.request_id = request_id_value
        self.detail = detail
        self.retry_after = retry_after
        self.__cause__ = cause


def problem(exception: ApiException) -> LakeholdProblemError:
    if exception is None:
        raise ValueError("exception is required")
    details = None
    if getattr(exception, "data", None) is not None:
        data = exception.data
        details = {
            "code": getattr(data, "code", None),
            "requestId": getattr(data, "request_id", None),
            "detail": getattr(data, "detail", None),
        }
    if not details or not details.get("code") or not details.get("requestId"):
        try:
            details = json.loads(exception.body or "{}")
        except (TypeError, json.JSONDecodeError):
            details = {}
    headers = exception.headers or {}
    return LakeholdProblemError(
        int(exception.status or 0),
        details.get("code") or "request_failed",
        details.get("requestId") or request_id(headers),
        details.get("detail"),
        _retry_after(headers),
        exception,
    )


@dataclass(frozen=True)
class RetryOptions:
    maximum_retries: int
    maximum_delay: float
    sleep: Callable[[float], None] = time.sleep

    def validate(self) -> None:
        if not 0 <= self.maximum_retries <= 10:
            raise ValueError("maximum_retries must be between 0 and 10")
        if self.maximum_delay <= 0:
            raise ValueError("maximum_delay must be positive")


def execute_with_retry(
    call: Callable[[], T],
    options: RetryOptions,
    *,
    retry_safe: bool,
    cancelled: Callable[[], bool] = lambda: False,
) -> T:
    if call is None:
        raise ValueError("call is required")
    options.validate()
    attempt = 0
    while True:
        if cancelled():
            raise CancelledError("request retry was cancelled")
        try:
            return call()
        except ApiException as exception:
            parsed = problem(exception)
            if (
                not retry_safe
                or attempt >= options.maximum_retries
                or parsed.status not in {408, 429, 500, 502, 503, 504}
            ):
                raise parsed from exception
            fallback = 0.1 * (2 ** min(attempt, 8))
            delay = parsed.retry_after if parsed.retry_after is not None else fallback
            options.sleep(min(delay, options.maximum_delay))
            attempt += 1


@dataclass(frozen=True)
class CursorPage(Generic[T]):
    items: Iterable[T]
    next_cursor: Optional[str]


def paginate(loader: Callable[[Optional[str]], CursorPage[T]]) -> Generator[T, None, None]:
    if loader is None:
        raise ValueError("page loader is required")
    cursor = None
    while True:
        page = loader(cursor)
        items = tuple(page.items)
        if not items and page.next_cursor is not None:
            raise ValueError("a cursor page cannot be empty while advertising another page")
        yield from items
        cursor = page.next_cursor
        if cursor is None:
            return


@dataclass(frozen=True)
class StreamEvent:
    """One immutable NDJSON record from a LakeHold query or CDC stream."""

    type: str
    payload: Mapping[str, object]


def stream_query(
    client: ApiClient,
    *,
    tenant: str,
    catalog: str,
    sql: str,
    cancelled: Callable[[], bool] = lambda: False,
) -> Iterator[StreamEvent]:
    """Yield a read-only SQL result without materialising the complete response."""
    if not sql or not sql.strip():
        raise ValueError("query source is required")
    path = _stream_path(client, tenant, catalog, "query:stream")
    return _stream(
        client,
        "POST",
        path,
        expected_first_type="schema",
        body=json.dumps({"sql": sql}).encode("utf-8"),
        cancelled=cancelled,
    )


def stream_changes(
    client: ApiClient,
    *,
    tenant: str,
    catalog: str,
    table: str,
    from_snapshot: int,
    schema: str = "main",
    to_snapshot: Optional[int] = None,
    page_size: int = 1000,
    cursor: Optional[str] = None,
    cancelled: Callable[[], bool] = lambda: False,
) -> Iterator[StreamEvent]:
    """Yield a finite CDC range whose omitted upper snapshot is frozen by the server."""
    if not table or not table.strip() or not schema or not schema.strip():
        raise ValueError("table and schema are required")
    if from_snapshot < 0 or not 1 <= page_size <= 10_000:
        raise ValueError("from_snapshot must be non-negative and page_size must be between 1 and 10000")
    query = {
        "table": table,
        "schema": schema,
        "fromSnapshot": from_snapshot,
        "pageSize": page_size,
    }
    if to_snapshot is not None:
        query["toSnapshot"] = to_snapshot
    if cursor:
        query["cursor"] = cursor
    path = f"{_stream_path(client, tenant, catalog, 'changes:stream')}?{urlencode(query)}"
    return _stream(
        client,
        "GET",
        path,
        expected_first_type="stream",
        body=None,
        cancelled=cancelled,
    )


def _stream(
    client: ApiClient,
    method: str,
    url: str,
    *,
    expected_first_type: str,
    body: Optional[bytes],
    cancelled: Callable[[], bool],
) -> Iterator[StreamEvent]:
    if client is None:
        raise ValueError("client is required")
    token = client.configuration.access_token
    if not token:
        raise ValueError("the ApiClient configuration needs an access_token")
    headers = {
        "Accept": "application/x-ndjson",
        "Authorization": f"Bearer {token}",
        "User-Agent": SDK_USER_AGENT,
    }
    if body is not None:
        headers["Content-Type"] = "application/json"

    timeout = getattr(client, "_lakehold_request_timeout", None)
    response = client.rest_client.pool_manager.request(
        method,
        url,
        body=body,
        headers=headers,
        preload_content=False,
        timeout=timeout,
    )
    try:
        if not 200 <= response.status < 300:
            raw = response.read(1024 * 1024)
            exception = ApiException(
                status=response.status,
                reason=response.reason,
                body=raw.decode("utf-8", errors="replace"),
            )
            exception.headers = response.headers
            raise problem(exception)

        buffer = bytearray()
        first = True
        completed = False
        for chunk in response.stream(amt=64 * 1024, decode_content=True):
            if cancelled():
                raise CancelledError("stream consumption was cancelled")
            buffer.extend(chunk)
            if len(buffer) > 64 * 1024 * 1024:
                raise ValueError("a LakeHold stream record exceeded the 64 MiB client ceiling")
            while b"\n" in buffer:
                raw, _, remaining = buffer.partition(b"\n")
                buffer = bytearray(remaining)
                if not raw.strip():
                    continue
                payload = json.loads(raw)
                record_type = payload.get("type")
                if not isinstance(record_type, str) or not record_type:
                    raise ValueError("a LakeHold stream record has no type discriminator")
                if first and record_type != expected_first_type:
                    raise ValueError(
                        f"expected the stream to begin with {expected_first_type!r}, not {record_type!r}"
                    )
                first = False
                if record_type == "error":
                    cause = ApiException(status=200, reason="stream failed", body=raw.decode("utf-8"))
                    raise LakeholdProblemError(
                        200,
                        str(payload.get("code") or "stream_failed"),
                        payload.get("requestId"),
                        payload.get("detail"),
                        None,
                        cause,
                    )
                completed = record_type == "complete"
                yield StreamEvent(record_type, payload)
        if buffer.strip():
            raise EOFError("the LakeHold stream ended with a partial NDJSON record")
        if not completed:
            raise EOFError("the LakeHold stream ended without a completion record")
    finally:
        response.release_conn()


def _stream_path(client: ApiClient, tenant: str, catalog: str, operation: str) -> str:
    if not tenant or not tenant.strip() or not catalog or not catalog.strip():
        raise ValueError("tenant and catalog are required")
    base = client.configuration.host.rstrip("/")
    return (
        f"{base}/api/v1/tenants/{quote(tenant, safe='')}/catalogs/"
        f"{quote(catalog, safe='')}/{operation}"
    )


@dataclass(frozen=True)
class OperationSnapshot(Generic[T]):
    status: str
    result: Optional[T] = None
    error: Optional[str] = None


class LakeholdOperationError(RuntimeError):
    def __init__(self, status: str, error: Optional[str]) -> None:
        super().__init__(error or f"the operation ended with status '{status}'")
        self.status = status


def wait_for_operation(
    loader: Callable[[], OperationSnapshot[T]],
    *,
    timeout: float,
    poll_interval: float,
    cancelled: Callable[[], bool] = lambda: False,
    sleep: Callable[[float], None] = time.sleep,
) -> OperationSnapshot[T]:
    if loader is None:
        raise ValueError("operation loader is required")
    if timeout <= 0 or poll_interval <= 0:
        raise ValueError("timeout and poll_interval must be positive")
    deadline = time.monotonic() + timeout
    while True:
        if cancelled():
            raise CancelledError("operation polling was cancelled")
        operation = loader()
        status = operation.status.lower()
        if status == "succeeded":
            return operation
        if status in {"failed", "indeterminate"}:
            raise LakeholdOperationError(operation.status, operation.error)
        if status not in {"queued", "running"}:
            raise ValueError(f"unknown operation status '{operation.status}'")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("the operation did not complete before the timeout")
        sleep(min(poll_interval, remaining))


def _retry_after(headers) -> Optional[float]:
    value = None
    for key, candidate in headers.items():
        if key.lower() == "retry-after":
            value = candidate[0] if isinstance(candidate, (list, tuple)) else candidate
            break
    if value is None:
        return None
    try:
        seconds = int(str(value).strip())
        return float(seconds) if seconds >= 0 else None
    except ValueError:
        try:
            when = parsedate_to_datetime(str(value))
            if when.tzinfo is None:
                when = when.replace(tzinfo=timezone.utc)
            return max(0.0, (when - datetime.now(timezone.utc)).total_seconds())
        except (TypeError, ValueError, OverflowError):
            return None


__all__ = [
    "CursorPage",
    "CancelledError",
    "LakeholdOperationError",
    "LakeholdApiClient",
    "LakeholdProblemError",
    "OperationSnapshot",
    "REQUEST_ID_HEADER",
    "RetryOptions",
    "SDK_USER_AGENT",
    "SDK_VERSION",
    "StreamEvent",
    "configure",
    "create_idempotency_key",
    "execute_with_retry",
    "paginate",
    "problem",
    "request_id",
    "stream_changes",
    "stream_query",
    "validate_idempotency_key",
    "wait_for_operation",
]
