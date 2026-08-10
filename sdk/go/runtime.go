package lakehold

import (
	"bufio"
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const (
	SDKVersion      = "0.1.0"
	SDKUserAgent    = "lakehold-sdk/" + SDKVersion + " (go)"
	RequestIDHeader = "X-Request-Id"
	maximumDuration = time.Duration(1<<63 - 1)
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
	var body []byte
	if withBody, ok := err.(interface{ Body() []byte }); ok {
		body = withBody.Body()
	}
	return parseProblemBody(response, body, err, "request_failed")
}

func parseProblemBody(response *http.Response, body []byte, err error, fallbackCode string) *ProblemError {
	problem := &ProblemError{Code: fallbackCode, Cause: err}
	if response != nil {
		problem.Status = response.StatusCode
		problem.RequestID = response.Header.Get(RequestIDHeader)
		if delay, present := parseRetryAfter(response.Header.Get("Retry-After"), time.Now()); present {
			problem.RetryAfter = delay
		}
	}
	var details PublicApiProblemDetails
	if len(body) > 0 && json.Unmarshal(body, &details) == nil {
		if details.Code != "" {
			problem.Code = details.Code
		}
		if details.RequestId != "" {
			problem.RequestID = details.RequestId
		}
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
		exponent := attempt
		if exponent > 8 {
			exponent = 8
		}
		delay := 100 * time.Millisecond * time.Duration(1<<exponent)
		if response != nil {
			if advertised, present := parseRetryAfter(response.Header.Get("Retry-After"), time.Now()); present {
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

// StreamEvent is one immutable NDJSON record from a query or CDC stream.
type StreamEvent struct {
	Type    string
	Payload json.RawMessage
}

// StreamHandler consumes one stream record. Returning an error stops the HTTP read immediately.
type StreamHandler func(StreamEvent) error

// StreamQuery consumes a read-only SQL result without materialising the complete result set.
func StreamQuery(
	ctx context.Context,
	client *http.Client,
	baseURL string,
	bearerToken string,
	tenant string,
	catalog string,
	sql string,
	handler StreamHandler,
) error {
	if strings.TrimSpace(sql) == "" {
		return errors.New("query source is required")
	}
	body, err := json.Marshal(map[string]string{"sql": sql})
	if err != nil {
		return fmt.Errorf("encode streaming query: %w", err)
	}
	endpoint, err := streamURL(baseURL, tenant, catalog, "query:stream")
	if err != nil {
		return err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("create streaming query request: %w", err)
	}
	request.Header.Set("Content-Type", "application/json")
	return consumeStream(client, request, bearerToken, "schema", handler)
}

// StreamChanges consumes a finite CDC range. The server freezes an omitted upper snapshot once.
func StreamChanges(
	ctx context.Context,
	client *http.Client,
	baseURL string,
	bearerToken string,
	tenant string,
	catalog string,
	table string,
	fromSnapshot int64,
	schema string,
	toSnapshot *int64,
	pageSize int,
	cursor string,
	handler StreamHandler,
) error {
	if strings.TrimSpace(table) == "" || strings.TrimSpace(schema) == "" {
		return errors.New("table and schema are required")
	}
	if fromSnapshot < 0 || pageSize < 1 || pageSize > 10_000 {
		return errors.New("fromSnapshot must be non-negative and pageSize must be between 1 and 10000")
	}
	endpoint, err := streamURL(baseURL, tenant, catalog, "changes:stream")
	if err != nil {
		return err
	}
	parsed, _ := url.Parse(endpoint)
	query := parsed.Query()
	query.Set("table", table)
	query.Set("schema", schema)
	query.Set("fromSnapshot", strconv.FormatInt(fromSnapshot, 10))
	query.Set("pageSize", strconv.Itoa(pageSize))
	if toSnapshot != nil {
		query.Set("toSnapshot", strconv.FormatInt(*toSnapshot, 10))
	}
	if cursor != "" {
		query.Set("cursor", cursor)
	}
	parsed.RawQuery = query.Encode()
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, parsed.String(), nil)
	if err != nil {
		return fmt.Errorf("create CDC stream request: %w", err)
	}
	return consumeStream(client, request, bearerToken, "stream", handler)
}

func consumeStream(
	client *http.Client,
	request *http.Request,
	bearerToken string,
	expectedFirstType string,
	handler StreamHandler,
) error {
	if client == nil || handler == nil {
		return errors.New("HTTP client and stream handler are required")
	}
	if strings.TrimSpace(bearerToken) == "" {
		return errors.New("bearer token is required")
	}
	request.Header.Set("Authorization", "Bearer "+bearerToken)
	request.Header.Set("Accept", "application/x-ndjson")
	request.Header.Set("User-Agent", SDKUserAgent)
	response, err := client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		body, readErr := io.ReadAll(io.LimitReader(response.Body, 1<<20))
		problem := parseProblemBody(response, body, readErr, "stream_request_failed")
		if problem.Detail == "" && len(body) > 0 {
			problem.Detail = string(body)
		}
		return problem
	}

	// One record is bounded independently of the complete stream. The ceiling prevents a peer from
	// growing a client buffer indefinitely while still allowing wide analytical rows.
	scanner := bufio.NewScanner(response.Body)
	scanner.Buffer(make([]byte, 64*1024), 64*1024*1024)
	first := true
	completed := false
	for scanner.Scan() {
		line := bytes.TrimSpace(scanner.Bytes())
		if len(line) == 0 {
			continue
		}
		var header struct {
			Type      string `json:"type"`
			Code      string `json:"code"`
			RequestID string `json:"requestId"`
			Detail    string `json:"detail"`
		}
		if err := json.Unmarshal(line, &header); err != nil {
			return fmt.Errorf("decode LakeHold stream record: %w", err)
		}
		if header.Type == "" {
			return errors.New("a LakeHold stream record has no type discriminator")
		}
		if first && header.Type != expectedFirstType {
			return fmt.Errorf("expected the stream to begin with %q, not %q", expectedFirstType, header.Type)
		}
		first = false
		if header.Type == "error" {
			return &ProblemError{Status: 200, Code: header.Code, RequestID: header.RequestID, Detail: header.Detail}
		}
		payload := append(json.RawMessage(nil), line...)
		if err := handler(StreamEvent{Type: header.Type, Payload: payload}); err != nil {
			return err
		}
		completed = header.Type == "complete"
	}
	if err := scanner.Err(); err != nil {
		return fmt.Errorf("read LakeHold stream: %w", err)
	}
	if !completed {
		return io.ErrUnexpectedEOF
	}
	return nil
}

func streamURL(baseURL, tenant, catalog, operation string) (string, error) {
	parsed, err := url.Parse(baseURL)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return "", errors.New("baseURL must be an absolute HTTP(S) URL")
	}
	if strings.TrimSpace(tenant) == "" || strings.TrimSpace(catalog) == "" {
		return "", errors.New("tenant and catalog are required")
	}
	escapedPath := strings.TrimRight(parsed.EscapedPath(), "/") + "/api/v1/tenants/" + url.PathEscape(tenant) +
		"/catalogs/" + url.PathEscape(catalog) + "/" + url.PathEscape(operation)
	decodedPath, err := url.PathUnescape(escapedPath)
	if err != nil {
		return "", fmt.Errorf("construct stream URL: %w", err)
	}
	parsed.Path = decodedPath
	parsed.RawPath = escapedPath
	parsed.RawQuery = ""
	return parsed.String(), nil
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
	pollContext, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	for {
		if err := pollContext.Err(); err != nil {
			return zero, err
		}
		operation, err := loader(pollContext)
		if err != nil {
			return zero, err
		}
		if err := pollContext.Err(); err != nil {
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
		case <-pollContext.Done():
			timer.Stop()
			return zero, pollContext.Err()
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

func parseRetryAfter(value string, now time.Time) (time.Duration, bool) {
	if strings.TrimSpace(value) == "" {
		return 0, false
	}
	if seconds, err := strconv.ParseInt(strings.TrimSpace(value), 10, 64); err == nil {
		if seconds < 0 {
			return 0, false
		}
		if seconds > int64(maximumDuration/time.Second) {
			return maximumDuration, true
		}
		return time.Duration(seconds) * time.Second, true
	}
	when, err := http.ParseTime(value)
	if err != nil {
		return 0, false
	}
	if when.Before(now) {
		return 0, true
	}
	return when.Sub(now), true
}

func min(left, right int) int {
	if left < right {
		return left
	}
	return right
}
