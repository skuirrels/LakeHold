package io.lakehold.sdk.runtime;

import io.lakehold.sdk.ApiClient;
import io.lakehold.sdk.ApiException;
import io.lakehold.sdk.JSON;
import io.lakehold.sdk.model.PublicApiProblemDetails;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.IOException;
import java.time.Duration;
import java.time.Instant;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayDeque;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Objects;
import java.util.UUID;
import java.util.concurrent.CancellationException;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import okhttp3.HttpUrl;
import okhttp3.MediaType;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okio.BufferedSource;

/** Handwritten reliability helpers layered over the generated LakeHold v1 client. */
public final class LakeholdRuntime {
    private static final long MAXIMUM_STREAM_RECORD_BYTES = 64L * 1024L * 1024L;
    private static final long MAXIMUM_ERROR_BODY_BYTES = 1024L * 1024L;
    public static final String VERSION = "0.1.0";
    public static final String USER_AGENT = "lakehold-sdk/" + VERSION + " (java)";
    public static final String REQUEST_ID_HEADER = "X-Request-Id";

    private LakeholdRuntime() {
    }

    public static ApiClient configure(ApiClient client, Duration timeout) {
        Objects.requireNonNull(client, "client");
        int milliseconds = exactMilliseconds(timeout);
        return client
            .setUserAgent(USER_AGENT)
            .setConnectTimeout(milliseconds)
            .setReadTimeout(milliseconds)
            .setWriteTimeout(milliseconds);
    }

    public static String createIdempotencyKey() {
        return UUID.randomUUID().toString().replace("-", "");
    }

    public static String validateIdempotencyKey(String value) {
        Objects.requireNonNull(value, "value");
        if (value.length() < 16 || value.length() > 128
            || value.chars().anyMatch(character -> character < '!' || character > '~')) {
            throw new IllegalArgumentException(
                "An idempotency key must contain 16-128 visible ASCII characters.");
        }
        return value;
    }

    public static String requestId(Map<String, List<String>> headers) {
        if (headers == null) {
            return null;
        }
        return headers.entrySet().stream()
            .filter(entry -> REQUEST_ID_HEADER.equalsIgnoreCase(entry.getKey()))
            .flatMap(entry -> entry.getValue().stream())
            .findFirst()
            .orElse(null);
    }

    public static ProblemException problem(ApiException exception) {
        Objects.requireNonNull(exception, "exception");
        try {
            PublicApiProblemDetails details = JSON.getGson().fromJson(
                exception.getResponseBody(), PublicApiProblemDetails.class);
            if (details != null && details.getCode() != null && details.getRequestId() != null) {
                return new ProblemException(
                    exception.getCode(),
                    details.getCode(),
                    details.getRequestId(),
                    details.getDetail(),
                    retryAfter(exception.getResponseHeaders()),
                    exception);
            }
        } catch (RuntimeException ignored) {
            // Malformed non-problem bodies remain available through the generated ApiException.
        }
        return new ProblemException(
            exception.getCode(),
            "request_failed",
            requestId(exception.getResponseHeaders()),
            null,
            retryAfter(exception.getResponseHeaders()),
            exception);
    }

    public static <T> Iterator<T> paginate(PageLoader<T> loader) {
        return new CursorIterator<>(loader);
    }

    /** Streams a read-only SQL result without materialising the complete response. */
    public static void streamQuery(
        ApiClient client,
        String bearerToken,
        String tenant,
        String catalog,
        String sql,
        Consumer<StreamEvent> handler) throws IOException, ApiException {
        Objects.requireNonNull(sql, "sql");
        if (sql.trim().isEmpty()) {
            throw new IllegalArgumentException("Query source is required.");
        }
        JsonObject body = new JsonObject();
        body.addProperty("sql", sql);
        HttpUrl url = streamUrl(client, tenant, catalog, "query:stream").build();
        Request request = request(url, bearerToken)
            .post(RequestBody.create(body.toString(), MediaType.parse("application/json")))
            .build();
        consumeStream(client, request, "schema", handler);
    }

    /** Streams a finite CDC range whose omitted upper snapshot is frozen by the server. */
    public static void streamChanges(
        ApiClient client,
        String bearerToken,
        String tenant,
        String catalog,
        String table,
        long fromSnapshot,
        String schema,
        Long toSnapshot,
        int pageSize,
        String cursor,
        Consumer<StreamEvent> handler) throws IOException, ApiException {
        if (fromSnapshot < 0 || pageSize < 1 || pageSize > 10_000) {
            throw new IllegalArgumentException(
                "fromSnapshot must be non-negative and pageSize must be between 1 and 10000.");
        }
        HttpUrl.Builder url = streamUrl(client, tenant, catalog, "changes:stream")
            .addQueryParameter("table", required(table, "table"))
            .addQueryParameter("schema", required(schema, "schema"))
            .addQueryParameter("fromSnapshot", Long.toString(fromSnapshot))
            .addQueryParameter("pageSize", Integer.toString(pageSize));
        if (toSnapshot != null) {
            url.addQueryParameter("toSnapshot", toSnapshot.toString());
        }
        if (cursor != null && !cursor.trim().isEmpty()) {
            url.addQueryParameter("cursor", cursor);
        }
        consumeStream(client, request(url.build(), bearerToken).get().build(), "stream", handler);
    }

    private static void consumeStream(
        ApiClient client,
        Request request,
        String expectedFirstType,
        Consumer<StreamEvent> handler) throws IOException, ApiException {
        Objects.requireNonNull(client, "client");
        Objects.requireNonNull(handler, "handler");
        try (Response response = client.getHttpClient().newCall(request).execute()) {
            if (!response.isSuccessful()) {
                String body = response.body() == null
                    ? ""
                    : response.peekBody(MAXIMUM_ERROR_BODY_BYTES).string();
                throw new ApiException(response.code(), response.message(), response.headers().toMultimap(), body);
            }
            if (response.body() == null) {
                throw new IOException("The LakeHold stream response had no body.");
            }

            boolean first = true;
            boolean completed = false;
            try (BufferedSource source = response.body().source()) {
                while (!source.exhausted()) {
                    String line;
                    try {
                        line = source.readUtf8LineStrict(MAXIMUM_STREAM_RECORD_BYTES);
                    } catch (java.io.EOFException exception) {
                        throw new IOException(
                            "A LakeHold stream record was partial or exceeded the 64 MiB client ceiling.",
                            exception);
                    }
                    if (Thread.currentThread().isInterrupted()) {
                        throw new java.io.InterruptedIOException("Stream consumption was interrupted.");
                    }
                    if (line.trim().isEmpty()) {
                        continue;
                    }
                    JsonObject payload = JsonParser.parseString(line).getAsJsonObject();
                    String type = payload.has("type") ? payload.get("type").getAsString() : null;
                    if (type == null || type.trim().isEmpty()) {
                        throw new IOException("A LakeHold stream record has no type discriminator.");
                    }
                    if (first && !type.equals(expectedFirstType)) {
                        throw new IOException(
                            "Expected the stream to begin with '" + expectedFirstType + "', not '" + type + "'.");
                    }
                    first = false;
                    if (type.equals("error")) {
                        String code = payload.has("code") ? payload.get("code").getAsString() : "stream_failed";
                        String detail = payload.has("detail") ? payload.get("detail").getAsString() : code;
                        throw new ApiException(200, detail, response.headers().toMultimap(), line);
                    }
                    handler.accept(new StreamEvent(type, payload.deepCopy()));
                    completed = type.equals("complete");
                }
            }
            if (!completed) {
                throw new IOException("The LakeHold stream ended without a completion record.");
            }
        }
    }

    private static HttpUrl.Builder streamUrl(
        ApiClient client,
        String tenant,
        String catalog,
        String operation) {
        Objects.requireNonNull(client, "client");
        HttpUrl base = HttpUrl.parse(client.getBasePath());
        if (base == null) {
            throw new IllegalArgumentException("The ApiClient base path must be an absolute HTTP(S) URL.");
        }
        return base.newBuilder()
            .addPathSegments("api/v1/tenants")
            .addPathSegment(required(tenant, "tenant"))
            .addPathSegment("catalogs")
            .addPathSegment(required(catalog, "catalog"))
            .addPathSegment(operation);
    }

    private static Request.Builder request(HttpUrl url, String bearerToken) {
        return new Request.Builder()
            .url(url)
            .header("Authorization", "Bearer " + required(bearerToken, "bearerToken"))
            .header("Accept", "application/x-ndjson")
            .header("User-Agent", USER_AGENT);
    }

    private static String required(String value, String name) {
        Objects.requireNonNull(value, name);
        if (value.trim().isEmpty()) {
            throw new IllegalArgumentException(name + " is required.");
        }
        return value;
    }

    /** One immutable record from a LakeHold query or CDC stream. */
    public static final class StreamEvent {
        private final String type;
        private final JsonObject payload;

        public StreamEvent(String type, JsonObject payload) {
            this.type = Objects.requireNonNull(type, "type");
            this.payload = Objects.requireNonNull(payload, "payload").deepCopy();
        }

        public String type() { return type; }
        public JsonObject payload() { return payload.deepCopy(); }
    }

    public static <T> OperationSnapshot<T> waitForOperation(
        OperationLoader<T> loader,
        Duration timeout,
        Duration pollInterval,
        BooleanSupplier cancelled) throws ApiException, InterruptedException {
        Objects.requireNonNull(loader, "loader");
        Objects.requireNonNull(cancelled, "cancelled");
        Instant deadline = Instant.now().plus(requirePositive(timeout, "timeout"));
        Duration interval = requirePositive(pollInterval, "pollInterval");
        while (true) {
            if (cancelled.getAsBoolean()) {
                throw new CancellationException("Operation polling was cancelled.");
            }
            OperationSnapshot<T> operation = loader.load();
            String status = operation.status().toLowerCase(Locale.ROOT);
            if (status.equals("succeeded")) {
                return operation;
            }
            if (status.equals("failed") || status.equals("indeterminate")) {
                throw new OperationException(operation.status(), operation.error());
            }
            if (!status.equals("queued") && !status.equals("running")) {
                throw new IllegalStateException("Unknown operation status '" + operation.status() + "'.");
            }
            Instant now = Instant.now();
            if (!now.isBefore(deadline)) {
                throw new OperationTimeoutException("The operation did not complete before the timeout.");
            }
            long remainingMilliseconds = Duration.between(now, deadline).toMillis();
            if (remainingMilliseconds <= 0) {
                throw new OperationTimeoutException("The operation did not complete before the timeout.");
            }
            Thread.sleep(Math.min(exactMilliseconds(interval, "pollInterval"), remainingMilliseconds));
        }
    }

    public static final class Page<T> {
        private final List<T> items;
        private final String nextCursor;

        public Page(List<T> items, String nextCursor) {
            this.items = java.util.Collections.unmodifiableList(
                new java.util.ArrayList<>(Objects.requireNonNull(items, "items")));
            this.nextCursor = nextCursor;
        }

        public List<T> items() { return items; }
        public String nextCursor() { return nextCursor; }
    }

    @FunctionalInterface
    public interface PageLoader<T> {
        Page<T> load(String cursor) throws ApiException;
    }

    public static final class OperationSnapshot<T> {
        private final String status;
        private final T result;
        private final String error;

        public OperationSnapshot(String status, T result, String error) {
            this.status = Objects.requireNonNull(status, "status");
            this.result = result;
            this.error = error;
        }

        public String status() { return status; }
        public T result() { return result; }
        public String error() { return error; }
    }

    @FunctionalInterface
    public interface OperationLoader<T> {
        OperationSnapshot<T> load() throws ApiException;
    }

    @FunctionalInterface
    public interface ApiCall<T> {
        T execute() throws ApiException;
    }

    @FunctionalInterface
    public interface Sleeper {
        void sleep(Duration delay) throws InterruptedException;
    }

    public static final class RetryPolicy {
        private final int maximumRetries;
        private final Duration maximumDelay;
        private final Sleeper sleeper;

        public RetryPolicy(int maximumRetries, Duration maximumDelay) {
            this(maximumRetries, maximumDelay, delay -> Thread.sleep(delay.toMillis()));
        }

        public RetryPolicy(int maximumRetries, Duration maximumDelay, Sleeper sleeper) {
            if (maximumRetries < 0 || maximumRetries > 10) {
                throw new IllegalArgumentException("maximumRetries must be between 0 and 10.");
            }
            this.maximumRetries = maximumRetries;
            this.maximumDelay = requireAtLeastOneMillisecond(maximumDelay, "maximumDelay");
            this.sleeper = Objects.requireNonNull(sleeper, "sleeper");
        }

        public <T> T execute(ApiCall<T> call, boolean retrySafe) throws InterruptedException {
            Objects.requireNonNull(call, "call");
            for (int attempt = 0; ; attempt++) {
                try {
                    return call.execute();
                } catch (ApiException exception) {
                    if (!retrySafe || attempt >= maximumRetries || !isTransient(exception.getCode())) {
                        throw problem(exception);
                    }
                    Duration advertised = retryAfter(exception.getResponseHeaders());
                    Duration fallback = Duration.ofMillis(100L << Math.min(attempt, 8));
                    sleeper.sleep(min(advertised == null ? fallback : advertised, maximumDelay));
                }
            }
        }
    }

    public static final class ProblemException extends RuntimeException {
        private final int status;
        private final String code;
        private final String requestId;
        private final String detail;
        private final Duration retryAfter;

        private ProblemException(
            int status,
            String code,
            String requestId,
            String detail,
            Duration retryAfter,
            ApiException cause) {
            super(detail == null ? code : detail, cause);
            this.status = status;
            this.code = code;
            this.requestId = requestId;
            this.detail = detail;
            this.retryAfter = retryAfter;
        }

        public int status() { return status; }
        public String code() { return code; }
        public String requestId() { return requestId; }
        public String detail() { return detail; }
        public Duration retryAfter() { return retryAfter; }
    }

    public static final class OperationException extends RuntimeException {
        private final String status;

        private OperationException(String status, String error) {
            super(error == null ? "The operation ended with status '" + status + "'." : error);
            this.status = status;
        }

        public String status() { return status; }
    }

    public static final class OperationTimeoutException extends RuntimeException {
        private OperationTimeoutException(String message) { super(message); }
    }

    private static final class CursorIterator<T> implements Iterator<T> {
        private final PageLoader<T> loader;
        private final ArrayDeque<T> buffer = new ArrayDeque<>();
        private String cursor;
        private boolean finished;

        private CursorIterator(PageLoader<T> loader) {
            this.loader = Objects.requireNonNull(loader, "loader");
        }

        @Override
        public boolean hasNext() {
            loadIfNeeded();
            return !buffer.isEmpty();
        }

        @Override
        public T next() {
            if (!hasNext()) {
                throw new NoSuchElementException();
            }
            return buffer.removeFirst();
        }

        private void loadIfNeeded() {
            while (buffer.isEmpty() && !finished) {
                try {
                    Page<T> page = loader.load(cursor);
                    buffer.addAll(page.items());
                    cursor = page.nextCursor();
                    finished = cursor == null;
                    if (page.items().isEmpty() && !finished) {
                        throw new IllegalStateException("A cursor page cannot be empty while advertising another page.");
                    }
                } catch (ApiException exception) {
                    throw problem(exception);
                }
            }
        }
    }

    private static int exactMilliseconds(Duration duration) {
        return exactMilliseconds(duration, "timeout");
    }

    private static int exactMilliseconds(Duration duration, String name) {
        long milliseconds = requireAtLeastOneMillisecond(duration, name).toMillis();
        if (milliseconds > Integer.MAX_VALUE) {
            throw new IllegalArgumentException(name + " is too large.");
        }
        return (int)milliseconds;
    }

    private static Duration requireAtLeastOneMillisecond(Duration value, String name) {
        Duration duration = requirePositive(value, name);
        if (duration.toMillis() == 0) {
            throw new IllegalArgumentException(name + " must be at least one millisecond.");
        }
        return duration;
    }

    private static Duration requirePositive(Duration value, String name) {
        Objects.requireNonNull(value, name);
        if (value.isZero() || value.isNegative()) {
            throw new IllegalArgumentException(name + " must be positive.");
        }
        return value;
    }

    private static boolean isTransient(int status) {
        return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
    }

    private static Duration retryAfter(Map<String, List<String>> headers) {
        String value = header(headers, "Retry-After");
        if (value == null) {
            return null;
        }
        try {
            long seconds = Long.parseLong(value.trim());
            return seconds < 0 ? null : Duration.ofSeconds(seconds);
        } catch (NumberFormatException ignored) {
            try {
                Duration delay = Duration.between(
                    Instant.now(), ZonedDateTime.parse(value, DateTimeFormatter.RFC_1123_DATE_TIME).toInstant());
                return delay.isNegative() ? Duration.ZERO : delay;
            } catch (RuntimeException invalidDate) {
                return null;
            }
        }
    }

    private static String header(Map<String, List<String>> headers, String name) {
        if (headers == null) {
            return null;
        }
        return headers.entrySet().stream()
            .filter(entry -> name.equalsIgnoreCase(entry.getKey()))
            .flatMap(entry -> entry.getValue().stream())
            .findFirst()
            .orElse(null);
    }

    private static Duration min(Duration left, Duration right) {
        return left.compareTo(right) <= 0 ? left : right;
    }
}
