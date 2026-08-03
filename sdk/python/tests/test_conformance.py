from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from concurrent.futures import CancelledError
from threading import Thread
import http.client
import json
from pathlib import Path
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
