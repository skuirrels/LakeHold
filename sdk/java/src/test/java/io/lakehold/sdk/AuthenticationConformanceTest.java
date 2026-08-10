package io.lakehold.sdk;

import com.sun.net.httpserver.HttpServer;
import com.google.gson.JsonObject;
import io.lakehold.sdk.api.LakehouseApi;
import io.lakehold.sdk.model.AccessDto;
import io.lakehold.sdk.model.CreatedTokenDto;
import io.lakehold.sdk.runtime.LakeholdRuntime;
import java.io.InterruptedIOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class AuthenticationConformanceTest {
    private static final JsonObject FIXTURE = loadFixture();

    @Test
    void oneTimeTokenIsRedactedFromDiagnosticRendering() {
        CreatedTokenDto created = new CreatedTokenDto()
            .id(7)
            .name("automation")
            .token("one-time-secret");

        assertFalse(created.toString().contains("one-time-secret"));
        assertTrue(created.toString().contains("<redacted>"));
        assertEquals("one-time-secret", created.getToken());
    }

    @Test
    void sendsBearerAuthenticationAndDeserializesTheAccessContract() throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/api/v1/access", exchange -> {
            assertEquals("GET", exchange.getRequestMethod());
            assertEquals("Bearer test-token", exchange.getRequestHeaders().getFirst("Authorization"));
            assertEquals(LakeholdRuntime.USER_AGENT, exchange.getRequestHeaders().getFirst("User-Agent"));
            byte[] body = FIXTURE.getAsJsonObject("additiveAccess").toString()
                .getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(200, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
        });
        server.start();

        try {
            ApiClient client = LakeholdRuntime.configure(
                new ApiClient().setBasePath("http://127.0.0.1:" + server.getAddress().getPort()),
                Duration.ofSeconds(5));
            assertEquals(5_000, client.getHttpClient().callTimeoutMillis());
            client.setBearerToken("test-token");

            AccessDto access = new LakehouseApi(client).getApiV1Access().execute();

            assertEquals("authenticated", access.getMode());
            assertEquals("reader", access.getRole());
            assertEquals(true, access.getReadOnly());
            assertEquals(false, access.getSystemAdmin());
        } finally {
            server.stop(0);
        }
    }

    @Test
    void sharedReliabilityFixtureIsImplemented() throws Exception {
        String requestId = FIXTURE.get("requestId").getAsString();
        String problemBody = FIXTURE.getAsJsonObject("problem").toString();
        Map<String, List<String>> headers = new HashMap<>();
        headers.put("Retry-After", List.of("2"));
        headers.put("X-Request-Id", List.of(requestId));
        ApiException rateLimited = new ApiException(429, headers, problemBody);

        LakeholdRuntime.ProblemException problem = LakeholdRuntime.problem(rateLimited);
        assertEquals("rate_limited", problem.code());
        assertEquals(requestId, problem.requestId());
        assertEquals(Duration.ofSeconds(2), problem.retryAfter());

        AtomicInteger attempts = new AtomicInteger();
        List<Duration> delays = new ArrayList<>();
        LakeholdRuntime.RetryPolicy policy = new LakeholdRuntime.RetryPolicy(
            2, Duration.ofSeconds(30), delays::add);
        String value = policy.execute(() -> {
            if (attempts.incrementAndGet() < 3) {
                throw rateLimited;
            }
            return "ok";
        }, true);
        assertEquals("ok", value);
        assertEquals(3, attempts.get());
        assertEquals(List.of(Duration.ofSeconds(2), Duration.ofSeconds(2)), delays);

        AtomicInteger unsafeAttempts = new AtomicInteger();
        assertThrows(LakeholdRuntime.ProblemException.class, () -> policy.execute(() -> {
            unsafeAttempts.incrementAndGet();
            throw rateLimited;
        }, false));
        assertEquals(1, unsafeAttempts.get());

        Iterator<Integer> items = LakeholdRuntime.paginate(cursor -> cursor == null
            ? new LakeholdRuntime.Page<>(List.of(1, 2), "cursor-2")
            : new LakeholdRuntime.Page<>(List.of(3), null));
        List<Integer> collected = new ArrayList<>();
        items.forEachRemaining(collected::add);
        assertEquals(List.of(1, 2, 3), collected);

        ArrayDequeOperationLoader operations = new ArrayDequeOperationLoader();
        LakeholdRuntime.OperationSnapshot<String> completed = LakeholdRuntime.waitForOperation(
            operations::load, Duration.ofSeconds(1), Duration.ofMillis(1), () -> false);
        assertEquals("result", completed.result());
        assertThrows(CancellationException.class, () -> LakeholdRuntime.waitForOperation(
            operations::load, Duration.ofSeconds(1), Duration.ofMillis(1), () -> true));

        assertEquals(FIXTURE.get("idempotencyKey").getAsString(),
            LakeholdRuntime.validateIdempotencyKey(FIXTURE.get("idempotencyKey").getAsString()));
        for (String invalid : List.of(
            "too-short", "0123456789abcde ", "0123456789abcde\t", "0123456789abcdeé")) {
            assertThrows(IllegalArgumentException.class, () ->
                LakeholdRuntime.validateIdempotencyKey(invalid));
        }
        assertEquals(requestId, LakeholdRuntime.requestId(headers));

        AccessDto additive = JSON.getGson().fromJson(
            FIXTURE.getAsJsonObject("additiveAccess"), AccessDto.class);
        assertEquals("authenticated", additive.getMode());
    }

    @Test
    void rejectsDurationsThatWouldSilentlyBecomeZeroMilliseconds() {
        assertThrows(IllegalArgumentException.class, () ->
            LakeholdRuntime.configure(new ApiClient(), Duration.ofNanos(1)));
        assertThrows(IllegalArgumentException.class, () ->
            new LakeholdRuntime.RetryPolicy(1, Duration.ofNanos(1)));
        assertThrows(IllegalArgumentException.class, () ->
            LakeholdRuntime.waitForOperation(
                () -> new LakeholdRuntime.OperationSnapshot<>("running", null, null),
                Duration.ofSeconds(1),
                Duration.ofNanos(1),
                () -> false));
    }

    @Test
    void operationTimeoutBoundsAnOversizedPollInterval() {
        long started = System.nanoTime();

        assertThrows(LakeholdRuntime.OperationTimeoutException.class, () ->
            LakeholdRuntime.waitForOperation(
                () -> new LakeholdRuntime.OperationSnapshot<>("running", null, null),
                Duration.ofMillis(25),
                Duration.ofSeconds(5),
                () -> false));

        assertTrue(Duration.ofNanos(System.nanoTime() - started).compareTo(Duration.ofSeconds(1)) < 0);
    }

    @Test
    void operationTimeoutBoundsASlowLoader() {
        long started = System.nanoTime();

        assertThrows(LakeholdRuntime.OperationTimeoutException.class, () ->
            LakeholdRuntime.waitForOperation(
                () -> {
                    try {
                        Thread.sleep(5_000);
                    } catch (InterruptedException exception) {
                        Thread.currentThread().interrupt();
                    }
                    return new LakeholdRuntime.OperationSnapshot<>("running", null, null);
                },
                Duration.ofMillis(25),
                Duration.ofMillis(1),
                () -> false));

        assertTrue(Duration.ofNanos(System.nanoTime() - started).compareTo(Duration.ofSeconds(1)) < 0);
    }

    @Test
    void operationCancellationInterruptsASlowLoader() throws Exception {
        AtomicBoolean cancelled = new AtomicBoolean();
        CountDownLatch interrupted = new CountDownLatch(1);

        assertThrows(CancellationException.class, () ->
            LakeholdRuntime.waitForOperation(
                () -> {
                    cancelled.set(true);
                    try {
                        Thread.sleep(5_000);
                    } catch (InterruptedException exception) {
                        interrupted.countDown();
                        Thread.currentThread().interrupt();
                    }
                    return new LakeholdRuntime.OperationSnapshot<>("running", null, null);
                },
                Duration.ofSeconds(1),
                Duration.ofMillis(1),
                cancelled::get));

        assertTrue(interrupted.await(1, TimeUnit.SECONDS));
    }

    @Test
    void streamingFixtureIsConsumedIncrementally() throws Exception {
        byte[] fixture = Files.readAllBytes(Path.of("..", "conformance", "query-stream.ndjson"));
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        String[] observed = new String[3];
        server.createContext("/", exchange -> {
            observed[0] = exchange.getRequestMethod();
            observed[1] = exchange.getRequestURI().getRawPath();
            observed[2] = exchange.getRequestHeaders().getFirst("Authorization");
            exchange.getResponseHeaders().add("Content-Type", "application/x-ndjson");
            exchange.sendResponseHeaders(200, fixture.length);
            exchange.getResponseBody().write(fixture);
            exchange.close();
        });
        server.start();
        try {
            ApiClient client = LakeholdRuntime.configure(
                new ApiClient().setBasePath("http://127.0.0.1:" + server.getAddress().getPort()),
                Duration.ofSeconds(5));
            List<String> types = new ArrayList<>();
            LakeholdRuntime.streamQuery(
                client, "test-token", "tenant one", "catalog/one", "SELECT 1",
                event -> types.add(event.type()));
            assertEquals(List.of("schema", "row", "row", "complete"), types);
            assertEquals("POST", observed[0]);
            assertEquals(
                "/api/v1/tenants/tenant%20one/catalogs/catalog%2Fone/query:stream",
                observed[1]);
            assertEquals("Bearer test-token", observed[2]);
        } finally {
            server.stop(0);
        }
    }

    @Test
    void changeStreamingFixtureIsConsumedIncrementally() throws Exception {
        byte[] fixture = Files.readAllBytes(Path.of("..", "conformance", "change-stream.ndjson"));
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        String[] observed = new String[2];
        server.createContext("/", exchange -> {
            observed[0] = exchange.getRequestMethod();
            observed[1] = exchange.getRequestURI().getRawQuery();
            exchange.getResponseHeaders().add("Content-Type", "application/x-ndjson");
            exchange.sendResponseHeaders(200, fixture.length);
            exchange.getResponseBody().write(fixture);
            exchange.close();
        });
        server.start();
        try {
            ApiClient client = LakeholdRuntime.configure(
                new ApiClient().setBasePath("http://127.0.0.1:" + server.getAddress().getPort()),
                Duration.ofSeconds(5));
            List<String> types = new ArrayList<>();
            LakeholdRuntime.streamChanges(
                client, "test-token", "tenant one", "catalog/one", "orders current", 10,
                "main", 12L, 1, null, event -> types.add(event.type()));
            assertEquals(List.of("stream", "change", "change", "complete"), types);
            assertEquals("GET", observed[0]);
            assertTrue(observed[1].contains("table=orders%20current"));
            assertTrue(observed[1].contains("fromSnapshot=10"));
            assertTrue(observed[1].contains("toSnapshot=12"));
            assertTrue(observed[1].contains("pageSize=1"));
        } finally {
            server.stop(0);
        }
    }

    @Test
    void releasedServerStreamingConformance() throws Exception {
        String endpoint = System.getenv("LAKEHOLD_CONFORMANCE_URL");
        if (endpoint == null || endpoint.trim().isEmpty()) {
            return;
        }
        ApiClient client = LakeholdRuntime.configure(
            new ApiClient().setBasePath(endpoint), Duration.ofSeconds(30));
        List<String> types = new ArrayList<>();
        LakeholdRuntime.streamQuery(
            client,
            requiredEnvironment("LAKEHOLD_CONFORMANCE_TOKEN"),
            requiredEnvironment("LAKEHOLD_CONFORMANCE_TENANT"),
            requiredEnvironment("LAKEHOLD_CONFORMANCE_CATALOG"),
            "SELECT 1 AS conformance",
            event -> types.add(event.type()));
        assertEquals(List.of("schema", "row", "complete"), types);
    }

    @Test
    void releasedServerEnforcesTenantIsolation() throws Exception {
        String endpoint = System.getenv("LAKEHOLD_CONFORMANCE_URL");
        if (endpoint == null || endpoint.trim().isEmpty()) {
            return;
        }
        ApiClient client = LakeholdRuntime.configure(
            new ApiClient().setBasePath(endpoint), Duration.ofSeconds(30));
        ApiException exception = assertThrows(ApiException.class, () -> LakeholdRuntime.streamQuery(
            client,
            requiredEnvironment("LAKEHOLD_CONFORMANCE_TOKEN"),
            requiredEnvironment("LAKEHOLD_CONFORMANCE_TENANT") + "-other",
            requiredEnvironment("LAKEHOLD_CONFORMANCE_CATALOG"),
            "SELECT 1 AS conformance",
            event -> { }));
        LakeholdRuntime.ProblemException problem = LakeholdRuntime.problem(exception);
        assertEquals(404, problem.status());
        assertEquals("not_found", problem.code());
        assertTrue(problem.requestId() != null && !problem.requestId().isBlank());
    }

    @Test
    void releasedServerStreamingCanBeCancelled() throws Exception {
        String endpoint = System.getenv("LAKEHOLD_CONFORMANCE_URL");
        if (endpoint == null || endpoint.trim().isEmpty()) {
            return;
        }
        ApiClient client = LakeholdRuntime.configure(
            new ApiClient().setBasePath(endpoint), Duration.ofSeconds(30));
        try {
            assertThrows(InterruptedIOException.class, () -> LakeholdRuntime.streamQuery(
                client,
                requiredEnvironment("LAKEHOLD_CONFORMANCE_TOKEN"),
                requiredEnvironment("LAKEHOLD_CONFORMANCE_TENANT"),
                requiredEnvironment("LAKEHOLD_CONFORMANCE_CATALOG"),
                "SELECT * FROM range(1000000)",
                event -> {
                    if ("schema".equals(event.type())) {
                        Thread.currentThread().interrupt();
                    }
                }));
        } finally {
            Thread.interrupted();
        }
    }

    private static String requiredEnvironment(String name) {
        String value = System.getenv(name);
        if (value == null || value.trim().isEmpty()) {
            throw new IllegalStateException(
                name + " is required when released-server conformance is enabled.");
        }
        return value;
    }

    private static JsonObject loadFixture() {
        try {
            return JSON.getGson().fromJson(
                Files.readString(Path.of("..", "conformance", "runtime-fixture.json")),
                JsonObject.class);
        } catch (Exception exception) {
            throw new ExceptionInInitializerError(exception);
        }
    }

    private static final class ArrayDequeOperationLoader {
        private final java.util.ArrayDeque<LakeholdRuntime.OperationSnapshot<String>> states =
            new java.util.ArrayDeque<>(List.of(
                new LakeholdRuntime.OperationSnapshot<>("queued", null, null),
                new LakeholdRuntime.OperationSnapshot<>("running", null, null),
                new LakeholdRuntime.OperationSnapshot<>("succeeded", "result", null)));

        LakeholdRuntime.OperationSnapshot<String> load() {
            LakeholdRuntime.OperationSnapshot<String> current = states.pollFirst();
            return current == null
                ? new LakeholdRuntime.OperationSnapshot<>("succeeded", "result", null)
                : current;
        }
    }
}
