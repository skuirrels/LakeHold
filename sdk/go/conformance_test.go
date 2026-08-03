package lakehold

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

type runtimeFixture struct {
	IdempotencyKey string          `json:"idempotencyKey"`
	RequestID      string          `json:"requestId"`
	Problem        json.RawMessage `json:"problem"`
	AdditiveAccess json.RawMessage `json:"additiveAccess"`
}

var conformanceFixture = loadRuntimeFixture()

func TestBearerAuthenticationAndAccessContract(t *testing.T) {
	t.Parallel()
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodGet || request.URL.Path != "/api/v1/access" {
			t.Errorf("unexpected request: %s %s", request.Method, request.URL.Path)
		}
		if got := request.Header.Get("Authorization"); got != "Bearer test-token" {
			t.Errorf("unexpected Authorization header: %q", got)
		}
		if got := request.Header.Get("User-Agent"); got != SDKUserAgent {
			t.Errorf("unexpected User-Agent header: %q", got)
		}
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write(conformanceFixture.AdditiveAccess)
	}))
	defer server.Close()

	configuration := NewConfiguration()
	configuration.Servers = ServerConfigurations{{URL: server.URL}}
	if err := ConfigureRuntime(configuration, 5*time.Second); err != nil {
		t.Fatalf("ConfigureRuntime failed: %v", err)
	}
	client := NewAPIClient(configuration)
	ctx := context.WithValue(context.Background(), ContextAccessToken, "test-token")

	access, _, err := client.LakehouseAPI.GetApiV1Access(ctx).Execute()
	if err != nil {
		t.Fatalf("GetApiV1Access failed: %v", err)
	}
	if access.Mode != "authenticated" || access.Role != "reader" || !access.ReadOnly || access.SystemAdmin {
		t.Fatalf("unexpected access response: %#v", access)
	}
}

func TestSharedReliabilityFixture(t *testing.T) {
	t.Parallel()
	response := &http.Response{
		StatusCode: http.StatusTooManyRequests,
		Header: http.Header{
			"Retry-After":   []string{"2"},
			RequestIDHeader: []string{conformanceFixture.RequestID},
		},
	}
	apiError := GenericOpenAPIError{body: conformanceFixture.Problem, error: "429 Too Many Requests"}
	problem := ParseProblem(response, apiError)
	if problem.Code != "rate_limited" || problem.RequestID != conformanceFixture.RequestID || problem.RetryAfter != 2*time.Second {
		t.Fatalf("unexpected problem: %#v", problem)
	}

	attempts := 0
	var delays []time.Duration
	value, _, err := ExecuteWithRetry(context.Background(), RetryOptions{
		MaximumRetries: 2,
		MaximumDelay:   30 * time.Second,
		Sleep: func(_ context.Context, delay time.Duration) error {
			delays = append(delays, delay)
			return nil
		},
	}, true, func(context.Context) (string, *http.Response, error) {
		attempts++
		if attempts < 3 {
			return "", response, apiError
		}
		return "ok", &http.Response{StatusCode: http.StatusOK}, nil
	})
	if err != nil || value != "ok" || attempts != 3 || len(delays) != 2 || delays[0] != 2*time.Second {
		t.Fatalf("unexpected retry result: value=%q attempts=%d delays=%v err=%v", value, attempts, delays, err)
	}

	unsafeAttempts := 0
	_, _, err = ExecuteWithRetry(context.Background(), RetryOptions{
		MaximumRetries: 2,
		MaximumDelay:   30 * time.Second,
	}, false, func(context.Context) (string, *http.Response, error) {
		unsafeAttempts++
		return "", response, apiError
	})
	if err == nil || unsafeAttempts != 1 {
		t.Fatalf("unsafe request was retried: attempts=%d err=%v", unsafeAttempts, err)
	}

	pager, err := NewCursorPager(func(_ context.Context, cursor string) (CursorPage[int], error) {
		if cursor == "" {
			return CursorPage[int]{Items: []int{1, 2}, NextCursor: "cursor-2"}, nil
		}
		return CursorPage[int]{Items: []int{3}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	var items []int
	for {
		item, ok, nextErr := pager.Next(context.Background())
		if nextErr != nil {
			t.Fatal(nextErr)
		}
		if !ok {
			break
		}
		items = append(items, item)
	}
	if fmt.Sprint(items) != "[1 2 3]" {
		t.Fatalf("unexpected paged items: %v", items)
	}

	states := []string{"queued", "running", "succeeded"}
	index := 0
	operation, err := WaitForOperation(context.Background(), time.Second, time.Millisecond,
		func(context.Context) (OperationSnapshot[string], error) {
			status := states[index]
			index++
			return OperationSnapshot[string]{Status: status, Result: "result"}, nil
		})
	if err != nil || operation.Result != "result" {
		t.Fatalf("unexpected operation result: %#v err=%v", operation, err)
	}

	cancelled, cancel := context.WithCancel(context.Background())
	cancel()
	_, err = WaitForOperation(cancelled, time.Second, time.Millisecond,
		func(context.Context) (OperationSnapshot[string], error) {
			return OperationSnapshot[string]{Status: "running"}, nil
		})
	if err == nil {
		t.Fatal("cancelled operation polling did not fail")
	}

	if err := ValidateIdempotencyKey(conformanceFixture.IdempotencyKey); err != nil {
		t.Fatal(err)
	}
	for _, invalid := range []string{"too-short", "0123456789abcde ", "0123456789abcde\t", "0123456789abcdeé"} {
		if err := ValidateIdempotencyKey(invalid); err == nil {
			t.Fatalf("invalid idempotency key was accepted: %q", invalid)
		}
	}
	var access AccessDto
	if err := json.Unmarshal(conformanceFixture.AdditiveAccess, &access); err != nil || access.Mode != "authenticated" {
		t.Fatalf("additive field was not tolerated: %#v err=%v", access, err)
	}
}

func TestOneTimeTokenIsRedactedFromDiagnosticRendering(t *testing.T) {
	t.Parallel()
	created := NewCreatedTokenDto(7, "automation", "one-time-secret")

	diagnostic := fmt.Sprint(created)
	if strings.Contains(diagnostic, "one-time-secret") {
		t.Fatalf("diagnostic rendering exposed the token: %s", diagnostic)
	}
	if !strings.Contains(diagnostic, "<redacted>") {
		t.Fatalf("diagnostic rendering did not identify the redaction: %s", diagnostic)
	}
	if created.Token != "one-time-secret" {
		t.Fatalf("token accessor was altered: %q", created.Token)
	}
}

func TestStreamingFixtureIsConsumedIncrementally(t *testing.T) {
	t.Parallel()
	fixture, err := os.ReadFile("../conformance/query-stream.ndjson")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodPost || request.URL.EscapedPath() != "/api/v1/tenants/tenant%20one/catalogs/catalog%2Fone/query:stream" {
			t.Errorf("unexpected request: %s %s", request.Method, request.URL.EscapedPath())
		}
		if request.Header.Get("Authorization") != "Bearer test-token" {
			t.Errorf("unexpected authorization: %q", request.Header.Get("Authorization"))
		}
		if request.Header.Get("User-Agent") != SDKUserAgent {
			t.Errorf("unexpected user agent: %q", request.Header.Get("User-Agent"))
		}
		writer.Header().Set("Content-Type", "application/x-ndjson")
		_, _ = writer.Write(fixture)
	}))
	defer server.Close()

	var types []string
	err = StreamQuery(
		context.Background(),
		server.Client(),
		server.URL,
		"test-token",
		"tenant one",
		"catalog/one",
		"SELECT 1",
		func(event StreamEvent) error {
			types = append(types, event.Type)
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	if fmt.Sprint(types) != "[schema row row complete]" {
		t.Fatalf("unexpected stream types: %v", types)
	}
}

func TestStreamingHandshakePreservesPublicProblem(t *testing.T) {
	t.Parallel()
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		writer.Header().Set("Content-Type", "application/problem+json")
		writer.Header().Set(RequestIDHeader, conformanceFixture.RequestID)
		writer.Header().Set("Retry-After", "2")
		writer.WriteHeader(http.StatusTooManyRequests)
		_, _ = writer.Write(conformanceFixture.Problem)
	}))
	defer server.Close()

	err := StreamQuery(
		context.Background(),
		server.Client(),
		server.URL,
		"test-token",
		"tenant",
		"catalog",
		"SELECT 1",
		func(StreamEvent) error { return nil })

	var problem *ProblemError
	if !errors.As(err, &problem) {
		t.Fatalf("expected ProblemError, got %T: %v", err, err)
	}
	if problem.Status != http.StatusTooManyRequests || problem.Code != "rate_limited" ||
		problem.RequestID != conformanceFixture.RequestID || problem.RetryAfter != 2*time.Second {
		t.Fatalf("unexpected stream problem: %#v", problem)
	}
}

func TestChangeStreamingFixtureIsConsumedIncrementally(t *testing.T) {
	t.Parallel()
	fixture, err := os.ReadFile("../conformance/change-stream.ndjson")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.Method != http.MethodGet || request.URL.EscapedPath() != "/api/v1/tenants/tenant%20one/catalogs/catalog%2Fone/changes:stream" {
			t.Errorf("unexpected request: %s %s", request.Method, request.URL.EscapedPath())
		}
		query := request.URL.Query()
		if query.Get("table") != "orders current" || query.Get("fromSnapshot") != "10" || query.Get("toSnapshot") != "12" || query.Get("pageSize") != "1" {
			t.Errorf("unexpected query: %s", request.URL.RawQuery)
		}
		writer.Header().Set("Content-Type", "application/x-ndjson")
		_, _ = writer.Write(fixture)
	}))
	defer server.Close()

	upper := int64(12)
	var types []string
	err = StreamChanges(
		context.Background(), server.Client(), server.URL, "test-token", "tenant one", "catalog/one",
		"orders current", 10, "main", &upper, 1, "", func(event StreamEvent) error {
			types = append(types, event.Type)
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	if fmt.Sprint(types) != "[stream change change complete]" {
		t.Fatalf("unexpected stream types: %v", types)
	}
}

func TestReleasedServerStreamingConformance(t *testing.T) {
	endpoint := os.Getenv("LAKEHOLD_CONFORMANCE_URL")
	if endpoint == "" {
		t.Skip("LAKEHOLD_CONFORMANCE_URL is not configured")
	}
	token := requiredEnvironment(t, "LAKEHOLD_CONFORMANCE_TOKEN")
	tenant := requiredEnvironment(t, "LAKEHOLD_CONFORMANCE_TENANT")
	catalog := requiredEnvironment(t, "LAKEHOLD_CONFORMANCE_CATALOG")
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	var types []string
	err := StreamQuery(ctx, http.DefaultClient, endpoint, token, tenant, catalog, "SELECT 1 AS conformance", func(event StreamEvent) error {
		types = append(types, event.Type)
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if fmt.Sprint(types) != "[schema row complete]" {
		t.Fatalf("unexpected released-server stream: %v", types)
	}
}

func requiredEnvironment(t *testing.T, name string) string {
	t.Helper()
	value := os.Getenv(name)
	if value == "" {
		t.Fatalf("%s is required when released-server conformance is enabled", name)
	}
	return value
}

func loadRuntimeFixture() runtimeFixture {
	content, err := os.ReadFile("../conformance/runtime-fixture.json")
	if err != nil {
		panic(err)
	}
	var fixture runtimeFixture
	if err := json.Unmarshal(content, &fixture); err != nil {
		panic(err)
	}
	return fixture
}
