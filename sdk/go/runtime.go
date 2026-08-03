package lakehold

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

const (
	SDKVersion      = "0.1.0"
	SDKUserAgent    = "lakehold-sdk/" + SDKVersion + " (go)"
	RequestIDHeader = "X-Request-Id"
)

// ConfigureRuntime applies the supported user-agent and a whole-request timeout without mutating
// a caller-owned http.Client instance.
func ConfigureRuntime(configuration *Configuration, timeout time.Duration) error {
	if configuration == nil {
		return errors.New("configuration is required")
	}
	if timeout <= 0 {
		return errors.New("timeout must be positive")
	}
	configuration.UserAgent = SDKUserAgent
	client := configuration.HTTPClient
	if client == nil {
		client = http.DefaultClient
	}
	copy := *client
	copy.Timeout = timeout
	configuration.HTTPClient = &copy
	return nil
}

func NewIdempotencyKey() (string, error) {
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		return "", fmt.Errorf("create idempotency key: %w", err)
	}
	return hex.EncodeToString(value), nil
}

func ValidateIdempotencyKey(value string) error {
	if len(value) < 16 || len(value) > 128 || strings.IndexFunc(value, func(r rune) bool { return r < '!' || r > '~' }) >= 0 {
		return errors.New("an idempotency key must contain 16-128 visible ASCII characters")
	}
	return nil
}

type ProblemError struct {
	Status     int
	Code       string
	RequestID  string
	Detail     string
	RetryAfter time.Duration
	Cause      error
}

func (e *ProblemError) Error() string {
	if e.Detail != "" {
		return e.Detail
	}
	return e.Code
}

func (e *ProblemError) Unwrap() error { return e.Cause }

func ParseProblem(response *http.Response, err error) *ProblemError {
	problem := &ProblemError{Code: "request_failed", Cause: err}
	if response != nil {
		problem.Status = response.StatusCode
		problem.RequestID = response.Header.Get(RequestIDHeader)
		problem.RetryAfter = parseRetryAfter(response.Header.Get("Retry-After"), time.Now())
	}
	var body []byte
	if withBody, ok := err.(interface{ Body() []byte }); ok {
		body = withBody.Body()
	}
	var details PublicApiProblemDetails
	if len(body) > 0 && json.Unmarshal(body, &details) == nil && details.Code != "" && details.RequestId != "" {
		problem.Code = details.Code
		problem.RequestID = details.RequestId
		if details.Detail.IsSet() && details.Detail.Get() != nil {
			problem.Detail = *details.Detail.Get()
		}
	}
	return problem
}

type RetryOptions struct {
	MaximumRetries int
	MaximumDelay   time.Duration
	Sleep          func(context.Context, time.Duration) error
}

func (options RetryOptions) validate() error {
	if options.MaximumRetries < 0 || options.MaximumRetries > 10 {
		return errors.New("maximum retries must be between 0 and 10")
	}
	if options.MaximumDelay <= 0 {
		return errors.New("maximum delay must be positive")
	}
	return nil
}

type RetryCall[T any] func(context.Context) (T, *http.Response, error)

func ExecuteWithRetry[T any](
	ctx context.Context,
	options RetryOptions,
	retrySafe bool,
	call RetryCall[T],
) (T, *http.Response, error) {
	var zero T
	if err := options.validate(); err != nil {
		return zero, nil, err
	}
	if call == nil {
		return zero, nil, errors.New("call is required")
	}
	sleep := options.Sleep
	if sleep == nil {
		sleep = func(ctx context.Context, delay time.Duration) error {
			timer := time.NewTimer(delay)
			defer timer.Stop()
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-timer.C:
				return nil
			}
		}
	}
	for attempt := 0; ; attempt++ {
		value, response, err := call(ctx)
		if err == nil {
			return value, response, nil
		}
		if !retrySafe || attempt >= options.MaximumRetries || !transientStatus(statusCode(response)) {
			return zero, response, ParseProblem(response, err)
		}
		delay := 100 * time.Millisecond * time.Duration(1<<min(attempt, 8))
		if response != nil {
			if advertised := parseRetryAfter(response.Header.Get("Retry-After"), time.Now()); advertised > 0 {
				delay = advertised
			}
		}
		if delay > options.MaximumDelay {
			delay = options.MaximumDelay
		}
		if err := sleep(ctx, delay); err != nil {
			return zero, response, err
		}
	}
}

type CursorPage[T any] struct {
	Items      []T
	NextCursor string
}

type CursorPageLoader[T any] func(context.Context, string) (CursorPage[T], error)

type CursorPager[T any] struct {
	loader   CursorPageLoader[T]
	cursor   string
	items    []T
	index    int
	finished bool
}

func NewCursorPager[T any](loader CursorPageLoader[T]) (*CursorPager[T], error) {
	if loader == nil {
		return nil, errors.New("page loader is required")
	}
	return &CursorPager[T]{loader: loader}, nil
}

func (pager *CursorPager[T]) Next(ctx context.Context) (T, bool, error) {
	var zero T
	for pager.index >= len(pager.items) && !pager.finished {
		page, err := pager.loader(ctx, pager.cursor)
		if err != nil {
			return zero, false, err
		}
		if len(page.Items) == 0 && page.NextCursor != "" {
			return zero, false, errors.New("a cursor page cannot be empty while advertising another page")
		}
		pager.items = page.Items
		pager.index = 0
		pager.cursor = page.NextCursor
		pager.finished = page.NextCursor == ""
	}
	if pager.index >= len(pager.items) {
		return zero, false, nil
	}
	value := pager.items[pager.index]
	pager.index++
	return value, true, nil
}

type OperationSnapshot[T any] struct {
	Status string
	Result T
	Error  string
}

type OperationLoader[T any] func(context.Context) (OperationSnapshot[T], error)

func WaitForOperation[T any](
	ctx context.Context,
	timeout time.Duration,
	pollInterval time.Duration,
	loader OperationLoader[T],
) (OperationSnapshot[T], error) {
	var zero OperationSnapshot[T]
	if timeout <= 0 || pollInterval <= 0 {
		return zero, errors.New("timeout and poll interval must be positive")
	}
	if loader == nil {
		return zero, errors.New("operation loader is required")
	}
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	for {
		operation, err := loader(ctx)
		if err != nil {
			return zero, err
		}
		switch strings.ToLower(operation.Status) {
		case "succeeded":
			return operation, nil
		case "failed", "indeterminate":
			return zero, fmt.Errorf("operation %s: %s", operation.Status, operation.Error)
		case "queued", "running":
		default:
			return zero, fmt.Errorf("unknown operation status %q", operation.Status)
		}
		timer := time.NewTimer(pollInterval)
		select {
		case <-ctx.Done():
			timer.Stop()
			return zero, ctx.Err()
		case <-deadline.C:
			timer.Stop()
			return zero, context.DeadlineExceeded
		case <-timer.C:
		}
	}
}

func statusCode(response *http.Response) int {
	if response == nil {
		return 0
	}
	return response.StatusCode
}

func transientStatus(status int) bool {
	return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504
}

func parseRetryAfter(value string, now time.Time) time.Duration {
	if value == "" {
		return 0
	}
	if seconds, err := strconv.ParseInt(strings.TrimSpace(value), 10, 64); err == nil {
		if seconds < 0 {
			return 0
		}
		return time.Duration(seconds) * time.Second
	}
	when, err := http.ParseTime(value)
	if err != nil || when.Before(now) {
		return 0
	}
	return when.Sub(now)
}

func min(left, right int) int {
	if left < right {
		return left
	}
	return right
}
