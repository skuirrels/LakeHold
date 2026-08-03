package lakehold

import (
	"context"
	"encoding/json"
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
