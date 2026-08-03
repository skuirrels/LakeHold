from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from concurrent.futures import CancelledError
from threading import Thread
import http.client
import json
import os
from pathlib import Path
import time
import pytest

import lakehold_sdk
from lakehold_sdk.exceptions import ApiException
from lakehold_sdk.runtime import (
    CursorPage,
    LakeholdApiClient,
    OperationSnapshot,
    LakeholdProblemError,
    REQUEST_ID_HEADER,
    RetryOptions,
    SDK_USER_AGENT,
    execute_with_retry,
    paginate,
    problem,
    request_id,
    stream_changes,
    stream_query,
    validate_idempotency_key,
    wait_for_operation,
)


FIXTURE = json.loads(
    Path("../conformance/runtime-fixture.json").read_text(encoding="utf-8")
)


def test_debug_mode_keeps_wire_dumps_disabled():
    configuration = lakehold_sdk.Configuration(debug=True)

    assert configuration.debug is True
    assert http.client.HTTPConnection.debuglevel == 0


def test_one_time_token_is_redacted_from_diagnostic_rendering():
    created = lakehold_sdk.CreatedTokenDto(
        id=7,
        name="automation",
        token="one-time-secret",
    )

    assert "one-time-secret" not in repr(created)
    assert "one-time-secret" not in str(created)
    assert "one-time-secret" not in created.to_str()
    assert "<redacted>" in created.to_str()
    assert created.token == "one-time-secret"


class _AccessHandler(BaseHTTPRequestHandler):
    authorization = None

    def do_GET(self):  # noqa: N802 - required by BaseHTTPRequestHandler
        type(self).authorization = self.headers.get("Authorization")
        type(self).user_agent = self.headers.get("User-Agent")
        if self.path != "/api/v1/access":
            self.send_error(404)
            return

        body = json.dumps(FIXTURE["additiveAccess"]).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format, *_args):
        return


def test_bearer_authentication_and_access_contract():
    server = ThreadingHTTPServer(("127.0.0.1", 0), _AccessHandler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        configuration = lakehold_sdk.Configuration(
            host=f"http://127.0.0.1:{server.server_port}",
            access_token="test-token",
        )
        with LakeholdApiClient(configuration, timeout=5) as client:
            access = lakehold_sdk.LakehouseApi(client).get_api_v1_access()

        assert _AccessHandler.authorization == "Bearer test-token"
        assert _AccessHandler.user_agent == SDK_USER_AGENT
        assert access.mode == "authenticated"
        assert access.role == "reader"
        assert access.read_only is True
        assert access.system_admin is False
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def test_shared_reliability_fixture_is_implemented():
    error = ApiException(
        status=429,
        reason="Too Many Requests",
        body=json.dumps(FIXTURE["problem"]),
    )
    error.headers = {
        "Retry-After": "2",
        REQUEST_ID_HEADER: FIXTURE["requestId"],
    }
    parsed = problem(error)
    assert parsed.code == "rate_limited"
    assert parsed.request_id == FIXTURE["requestId"]
    assert parsed.retry_after == 2

    attempts = 0
    delays = []

    def transient_call():
        nonlocal attempts
        attempts += 1
        if attempts < 3:
            raise error
        return "ok"

    value = execute_with_retry(
        transient_call,
        RetryOptions(2, 30, delays.append),
        retry_safe=True,
    )
    assert value == "ok"
    assert attempts == 3
    assert delays == [2, 2]

    unsafe_attempts = 0

    def unsafe_call():
        nonlocal unsafe_attempts
        unsafe_attempts += 1
        raise error

    with pytest.raises(LakeholdProblemError):
        execute_with_retry(
            unsafe_call,
            RetryOptions(2, 30, delays.append),
            retry_safe=False,
        )
    assert unsafe_attempts == 1

    assert list(paginate(lambda cursor: CursorPage(
        [1, 2] if cursor is None else [3],
        "cursor-2" if cursor is None else None,
    ))) == [1, 2, 3]

    states = iter(FIXTURE["operation"]["states"])
    completed = wait_for_operation(
        lambda: OperationSnapshot(next(states), result="result"),
        timeout=1,
        poll_interval=0.001,
        sleep=lambda _: None,
    )
    assert completed.result == "result"

    with pytest.raises(CancelledError):
        wait_for_operation(
            lambda: OperationSnapshot("running"),
            timeout=1,
            poll_interval=0.001,
            cancelled=lambda: True,
        )

    started = time.monotonic()
    with pytest.raises(TimeoutError):
        wait_for_operation(
            lambda: OperationSnapshot("running"),
            timeout=0.02,
            poll_interval=5,
        )
    assert time.monotonic() - started < 0.5

    assert validate_idempotency_key(FIXTURE["idempotencyKey"]) == FIXTURE["idempotencyKey"]
    for invalid in ("too-short", "0123456789abcde ", "0123456789abcde\t", "0123456789abcdeé"):
        with pytest.raises(ValueError):
            validate_idempotency_key(invalid)
    assert request_id(error.headers) == FIXTURE["requestId"]
    additive = lakehold_sdk.AccessDto.from_dict(FIXTURE["additiveAccess"])
    assert additive.mode == "authenticated"


def test_default_request_timeout_is_applied_and_can_be_overridden():
    class RecordingRestClient:
        def __init__(self):
            self.timeouts = []

        def request(self, *_args, _request_timeout=None, **_kwargs):
            self.timeouts.append(_request_timeout)
            return object()

    client = LakeholdApiClient(lakehold_sdk.Configuration(), timeout=5)
    recorder = RecordingRestClient()
    client.rest_client = recorder

    client.call_api("GET", "http://lakehold.test/api/v1/access")
    client.call_api("GET", "http://lakehold.test/api/v1/access", _request_timeout=2)

    assert recorder.timeouts == [5.0, 2]


def test_streaming_fixture_is_consumed_incrementally():
    fixture = Path("../conformance/query-stream.ndjson").read_bytes()

    class Response:
        status = 200
        reason = "OK"
        headers = {"Content-Type": "application/x-ndjson"}

        def stream(self, **_kwargs):
            midpoint = len(fixture) // 2
            yield fixture[:midpoint]
            yield fixture[midpoint:]

        def release_conn(self):
            return None

    class Pool:
        def __init__(self):
            self.observed = None

        def request(self, method, url, **kwargs):
            self.observed = (method, url, kwargs)
            return Response()

    configuration = lakehold_sdk.Configuration(
        host="https://lakehold.test",
        access_token="test-token",
    )
    client = LakeholdApiClient(configuration, timeout=5)
    pool = Pool()
    client.rest_client.pool_manager = pool

    events = list(stream_query(
        client,
        tenant="tenant one",
        catalog="catalog/one",
        sql="SELECT 1",
    ))

    assert [event.type for event in events] == ["schema", "row", "row", "complete"]
    method, url, kwargs = pool.observed
    assert method == "POST"
    assert url == "https://lakehold.test/api/v1/tenants/tenant%20one/catalogs/catalog%2Fone/query:stream"
    assert kwargs["headers"]["Authorization"] == "Bearer test-token"


def test_streaming_cancellation_is_checked_between_buffered_records():
    fixture = Path("../conformance/query-stream.ndjson").read_bytes()

    class Response:
        status = 200
        reason = "OK"
        headers = {"Content-Type": "application/x-ndjson"}

        def stream(self, **_kwargs):
            yield fixture

        def release_conn(self):
            return None

    class Pool:
        def request(self, *_args, **_kwargs):
            return Response()

    client = LakeholdApiClient(lakehold_sdk.Configuration(
        host="https://lakehold.test",
        access_token="test-token",
    ), timeout=5)
    client.rest_client.pool_manager = Pool()
    cancelled = False
    events = stream_query(
        client,
        tenant="tenant",
        catalog="catalog",
        sql="SELECT 1",
        cancelled=lambda: cancelled,
    )

    assert next(events).type == "schema"
    cancelled = True
    with pytest.raises(CancelledError):
        next(events)


def test_change_streaming_fixture_is_consumed_incrementally():
    fixture = Path("../conformance/change-stream.ndjson").read_bytes()

    class Response:
        status = 200
        reason = "OK"
        headers = {"Content-Type": "application/x-ndjson"}

        def stream(self, **_kwargs):
            yield fixture

        def release_conn(self):
            return None

    class Pool:
        def request(self, method, url, **kwargs):
            self.observed = (method, url, kwargs)
            return Response()

    client = LakeholdApiClient(lakehold_sdk.Configuration(
        host="https://lakehold.test",
        access_token="test-token",
    ), timeout=5)
    pool = Pool()
    client.rest_client.pool_manager = pool

    events = list(stream_changes(
        client,
        tenant="tenant one",
        catalog="catalog/one",
        table="orders current",
        from_snapshot=10,
        to_snapshot=12,
        page_size=1,
    ))

    assert [event.type for event in events] == ["stream", "change", "change", "complete"]
    method, url, _ = pool.observed
    assert method == "GET"
    assert "table=orders+current" in url
    assert "fromSnapshot=10" in url
    assert "toSnapshot=12" in url
    assert "pageSize=1" in url


def test_released_server_streaming_conformance():
    endpoint, token, tenant, catalog = _released_server_settings()
    if endpoint is None:
        pytest.skip("LAKEHOLD_CONFORMANCE_URL is not configured")
    configuration = lakehold_sdk.Configuration(
        host=endpoint,
        access_token=token,
    )
    with LakeholdApiClient(configuration, timeout=30) as client:
        events = list(stream_query(
            client,
            tenant=tenant,
            catalog=catalog,
            sql="SELECT 1 AS conformance",
        ))

    assert [event.type for event in events] == ["schema", "row", "complete"]


def test_released_server_enforces_tenant_isolation():
    endpoint, token, tenant, catalog = _released_server_settings()
    if endpoint is None:
        pytest.skip("LAKEHOLD_CONFORMANCE_URL is not configured")
    configuration = lakehold_sdk.Configuration(host=endpoint, access_token=token)
    with LakeholdApiClient(configuration, timeout=30) as client:
        with pytest.raises(LakeholdProblemError) as captured:
            list(stream_query(
                client,
                tenant=f"{tenant}-other",
                catalog=catalog,
                sql="SELECT 1 AS conformance",
            ))

    assert captured.value.status == 404
    assert captured.value.code == "not_found"
    assert captured.value.request_id


def test_released_server_streaming_can_be_cancelled():
    endpoint, token, tenant, catalog = _released_server_settings()
    if endpoint is None:
        pytest.skip("LAKEHOLD_CONFORMANCE_URL is not configured")
    configuration = lakehold_sdk.Configuration(host=endpoint, access_token=token)
    cancelled = False
    with LakeholdApiClient(configuration, timeout=30) as client:
        events = stream_query(
            client,
            tenant=tenant,
            catalog=catalog,
            sql="SELECT * FROM range(1000000)",
            cancelled=lambda: cancelled,
        )
        assert next(events).type == "schema"
        cancelled = True
        with pytest.raises(CancelledError):
            next(events)


def _released_server_settings():
    endpoint = os.getenv("LAKEHOLD_CONFORMANCE_URL")
    if not endpoint:
        return None, None, None, None
    required = {
        name: os.getenv(name)
        for name in (
            "LAKEHOLD_CONFORMANCE_TOKEN",
            "LAKEHOLD_CONFORMANCE_TENANT",
            "LAKEHOLD_CONFORMANCE_CATALOG",
        )
    }
    missing = [name for name, value in required.items() if not value]
    assert not missing, f"missing released-server conformance settings: {', '.join(missing)}"
    return (
        endpoint.rstrip("/"),
        required["LAKEHOLD_CONFORMANCE_TOKEN"],
        required["LAKEHOLD_CONFORMANCE_TENANT"],
        required["LAKEHOLD_CONFORMANCE_CATALOG"],
    )
