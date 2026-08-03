"""Supported reliability helpers layered over the generated LakeHold v1 client."""

from __future__ import annotations

from collections.abc import Callable, Generator, Iterable
from concurrent.futures import CancelledError
from dataclasses import dataclass
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
import json
import math
import secrets
import time
from typing import Generic, Optional, TypeVar

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
        if time.monotonic() >= deadline:
            raise TimeoutError("the operation did not complete before the timeout")
        sleep(poll_interval)


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
    "configure",
    "create_idempotency_key",
    "execute_with_retry",
    "paginate",
    "problem",
    "request_id",
    "validate_idempotency_key",
    "wait_for_operation",
]
