package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"rightmenucheck.local/telemetry/internal/sessiontoken"
	"rightmenucheck.local/telemetry/internal/store"
)

const (
	testMachineUpper = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
	testMachineLower = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	testSessionA     = "00112233445566778899AABBCCDDEEFF"
	testSessionB     = "10112233445566778899AABBCCDDEEFF"
	validBody        = `{"machineId":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sessionId":"00112233445566778899AABBCCDDEEFF"}`
	adminToken       = "0123456789abcdef0123456789abcdef"
)

func TestAuthenticatedTelemetryProtocolUsesServerTime(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	now := time.Date(2026, 8, 31, 6, 7, 8, 901_000_000, time.UTC)
	firstToken := tokenFixture(1)
	server := New(dataStore, testOptions(&now, tokenQueue(firstToken)))

	start := perform(server.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.10:1234", "")
	if start.Code != http.StatusOK {
		t.Fatalf("start status = %d, body = %s", start.Code, start.Body.String())
	}
	var payload startResponse
	if err := json.Unmarshal(start.Body.Bytes(), &payload); err != nil {
		t.Fatal(err)
	}
	if payload.StartupCount != 1 || payload.SessionToken != firstToken.raw ||
		!payload.StartedAtUTC.Equal(now) {
		t.Fatalf("unexpected start response: %#v", payload)
	}
	if strings.Count(start.Body.String(), "sessionToken") != 1 {
		t.Fatalf("start response shape = %s", start.Body.String())
	}

	missing := perform(server.Handler(), http.MethodPost, heartbeatPath, validBody, "application/json", "203.0.113.10:1234", "")
	if missing.Code != http.StatusUnauthorized || missing.Header().Get("WWW-Authenticate") != "Bearer" {
		t.Fatalf("missing-token heartbeat = %d, %s", missing.Code, missing.Body.String())
	}
	forged := tokenFixture(9)
	invalid := perform(server.Handler(), http.MethodPost, heartbeatPath, validBody, "application/json", "203.0.113.10:1234", "Bearer "+forged.raw)
	if invalid.Code != http.StatusUnauthorized {
		t.Fatalf("forged-token heartbeat = %d, %s", invalid.Code, invalid.Body.String())
	}

	now = now.Add(15 * time.Second)
	heartbeat := perform(server.Handler(), http.MethodPost, heartbeatPath, validBody, "application/json; charset=utf-8", "203.0.113.10:1234", "Bearer "+firstToken.raw)
	if heartbeat.Code != http.StatusNoContent || heartbeat.Body.Len() != 0 {
		t.Fatalf("heartbeat = %d, %q", heartbeat.Code, heartbeat.Body.String())
	}
	now = now.Add(10 * time.Second)
	end := perform(server.Handler(), http.MethodPost, endPath, validBody, "application/json", "203.0.113.10:1234", "Bearer "+firstToken.raw)
	if end.Code != http.StatusNoContent {
		t.Fatalf("end = %d, %s", end.Code, end.Body.String())
	}

	summary := perform(server.Handler(), http.MethodGet, "/v1/admin/summary", "", "", "127.0.0.1:5000", "Bearer "+adminToken)
	if summary.Code != http.StatusOK {
		t.Fatalf("summary = %d, %s", summary.Code, summary.Body.String())
	}
	var summaryPayload summaryResponse
	if err := json.Unmarshal(summary.Body.Bytes(), &summaryPayload); err != nil {
		t.Fatal(err)
	}
	if summaryPayload.MachineCount != 1 || summaryPayload.StartupCount != 1 ||
		summaryPayload.NormalSessionCount != 1 || summaryPayload.TotalDurationMS != 25_000 {
		t.Fatalf("unexpected summary: %#v", summaryPayload)
	}
	sessions := perform(server.Handler(), http.MethodGet, "/v1/admin/sessions", "", "", "127.0.0.1:5000", "Bearer "+adminToken)
	if sessions.Code != http.StatusOK || strings.Contains(strings.ToLower(sessions.Body.String()), "token") {
		t.Fatalf("session query exposed token data: %d, %s", sessions.Code, sessions.Body.String())
	}
}

func TestResumeCreatesNewSegmentWithoutIncrementingStartup(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	now := time.Date(2026, 8, 31, 7, 0, 0, 0, time.UTC)
	firstToken := tokenFixture(2)
	rejectedToken := tokenFixture(9)
	secondToken := tokenFixture(3)
	server := New(dataStore, testOptions(&now, tokenQueue(firstToken, rejectedToken, secondToken)))

	start := perform(server.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.1:1", "")
	if start.Code != http.StatusOK {
		t.Fatalf("start = %d, %s", start.Code, start.Body.String())
	}
	now = now.Add(time.Hour)
	resumeBody := `{"machineId":"` + testMachineUpper + `","previousSessionId":"` +
		testSessionA + `","sessionId":"` + testSessionB + `"}`
	forged := perform(server.Handler(), http.MethodPost, resumePath, resumeBody, "application/json", "203.0.113.1:1", "Bearer "+tokenFixture(8).raw)
	if forged.Code != http.StatusUnauthorized {
		t.Fatalf("forged resume = %d, %s", forged.Code, forged.Body.String())
	}
	resume := perform(server.Handler(), http.MethodPost, resumePath, resumeBody, "application/json", "203.0.113.1:1", "Bearer "+firstToken.raw)
	if resume.Code != http.StatusOK {
		t.Fatalf("resume = %d, %s", resume.Code, resume.Body.String())
	}
	var payload startResponse
	if err := json.Unmarshal(resume.Body.Bytes(), &payload); err != nil {
		t.Fatal(err)
	}
	if payload.StartupCount != 1 || payload.SessionToken != secondToken.raw {
		t.Fatalf("resume response = %#v", payload)
	}

	secondBody := `{"machineId":"` + testMachineUpper + `","sessionId":"` + testSessionB + `"}`
	now = now.Add(5 * time.Second)
	heartbeat := perform(server.Handler(), http.MethodPost, heartbeatPath, secondBody, "application/json", "203.0.113.1:1", "Bearer "+secondToken.raw)
	if heartbeat.Code != http.StatusNoContent {
		t.Fatalf("resumed heartbeat = %d, %s", heartbeat.Code, heartbeat.Body.String())
	}
	end := perform(server.Handler(), http.MethodPost, endPath, secondBody, "application/json", "203.0.113.1:1", "Bearer "+secondToken.raw)
	if end.Code != http.StatusNoContent {
		t.Fatalf("resumed end = %d, %s", end.Code, end.Body.String())
	}
	summary, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if summary.StartupCount != 1 || summary.SessionCount != 2 ||
		summary.NormalSessionCount != 1 || summary.AbnormalSessionCount != 1 {
		t.Fatalf("resume summary = %#v", summary)
	}
}

func TestAdminRequiresTokenUnlessExplicitTestMode(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	now := time.Now()

	protected := New(dataStore, Options{Clock: func() time.Time { return now }, AdminToken: adminToken})
	for _, test := range []struct {
		name       string
		remote     string
		auth       string
		wantStatus int
	}{
		{"loopback missing", "127.0.0.1:1", "", http.StatusUnauthorized},
		{"remote missing", "203.0.113.1:1", "", http.StatusUnauthorized},
		{"loopback wrong", "127.0.0.1:1", "Bearer wrong", http.StatusUnauthorized},
		{"loopback valid", "127.0.0.1:1", "Bearer " + adminToken, http.StatusOK},
		{"remote valid", "203.0.113.1:1", "Bearer " + adminToken, http.StatusOK},
	} {
		t.Run(test.name, func(t *testing.T) {
			response := perform(protected.Handler(), http.MethodGet, "/v1/admin/summary", "", "", test.remote, test.auth)
			if response.Code != test.wantStatus {
				t.Fatalf("status = %d, body = %s", response.Code, response.Body.String())
			}
		})
	}

	defaultDenied := New(dataStore, Options{Clock: func() time.Time { return now }})
	if response := perform(defaultDenied.Handler(), http.MethodGet, "/v1/admin/summary", "", "", "127.0.0.1:1", ""); response.Code != http.StatusUnauthorized {
		t.Fatalf("default no-token loopback status = %d", response.Code)
	}
	testMode := New(dataStore, Options{
		Clock:                             func() time.Time { return now },
		AllowUnauthenticatedLoopbackAdmin: true,
	})
	if response := perform(testMode.Handler(), http.MethodGet, "/v1/admin/summary", "", "", "127.0.0.1:1", ""); response.Code != http.StatusOK {
		t.Fatalf("explicit loopback test mode status = %d", response.Code)
	}
	if response := perform(testMode.Handler(), http.MethodGet, "/v1/admin/summary", "", "", "203.0.113.1:1", ""); response.Code != http.StatusUnauthorized {
		t.Fatalf("test mode remote status = %d", response.Code)
	}
}

func TestGlobalTokenBucketLimitsSequentialTraffic(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	now := time.Date(2026, 8, 31, 8, 0, 0, 0, time.UTC)
	server := New(dataStore, Options{
		Clock:             func() time.Time { return now },
		RequestsPerSecond: 1,
		RequestBurst:      2,
	})

	for index := 0; index < 2; index++ {
		if response := perform(server.Handler(), http.MethodGet, "/health", "", "", "127.0.0.1:1", ""); response.Code != http.StatusOK {
			t.Fatalf("burst request %d = %d", index, response.Code)
		}
	}
	limited := perform(server.Handler(), http.MethodGet, "/health", "", "", "127.0.0.1:1", "")
	if limited.Code != http.StatusTooManyRequests || limited.Header().Get("Retry-After") != "1" {
		t.Fatalf("rate-limited response = %d, %s", limited.Code, limited.Body.String())
	}
	now = now.Add(time.Second)
	if response := perform(server.Handler(), http.MethodGet, "/health", "", "", "127.0.0.1:1", ""); response.Code != http.StatusOK {
		t.Fatalf("refilled request = %d", response.Code)
	}
}

func TestStoreRateAndCapacityErrorsHaveDistinctResponses(t *testing.T) {
	now := time.Date(2026, 8, 31, 9, 0, 0, 0, time.UTC)
	limits := store.DefaultLimits()
	limits.MaxActiveSessions = 1
	limits.NewSessionsPerMinute = 10
	dataStore := newLimitedTestStore(t, limits)
	defer dataStore.Close()
	server := New(dataStore, testOptions(&now, tokenQueue(tokenFixture(1), tokenFixture(2))))

	first := perform(server.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.1:1", "")
	if first.Code != http.StatusOK {
		t.Fatalf("first start = %d, %s", first.Code, first.Body.String())
	}
	secondBody := `{"machineId":"` + testMachineUpper + `","sessionId":"` + testSessionB + `"}`
	capacity := perform(server.Handler(), http.MethodPost, startPath, secondBody, "application/json", "203.0.113.1:1", "")
	if capacity.Code != http.StatusInsufficientStorage || !strings.Contains(capacity.Body.String(), "capacity_exceeded") {
		t.Fatalf("capacity response = %d, %s", capacity.Code, capacity.Body.String())
	}

	rateLimits := store.DefaultLimits()
	rateLimits.NewSessionsPerMinute = 1
	rateStore := newLimitedTestStore(t, rateLimits)
	defer rateStore.Close()
	rateServer := New(rateStore, testOptions(&now, tokenQueue(tokenFixture(3), tokenFixture(4))))
	if response := perform(rateServer.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.1:1", ""); response.Code != http.StatusOK {
		t.Fatalf("rate first start = %d", response.Code)
	}
	rateLimited := perform(rateServer.Handler(), http.MethodPost, startPath, secondBody, "application/json", "203.0.113.1:1", "")
	if rateLimited.Code != http.StatusTooManyRequests ||
		!strings.Contains(rateLimited.Body.String(), "new_session_rate_limited") {
		t.Fatalf("new-session rate response = %d, %s", rateLimited.Code, rateLimited.Body.String())
	}
}

func TestHandlerDeadlineCancelsStoreCallsAndReleasesSlot(t *testing.T) {
	dataStore := deadlineStore{}
	server := New(dataStore, Options{
		HandlerTimeout:    25 * time.Millisecond,
		MaxConcurrent:     1,
		RequestsPerSecond: 1000,
		RequestBurst:      1000,
		TokenGenerator:    tokenQueue(tokenFixture(1)),
	})

	started := time.Now()
	health := perform(server.Handler(), http.MethodGet, "/health", "", "", "127.0.0.1:1", "")
	if health.Code != http.StatusGatewayTimeout || time.Since(started) > time.Second {
		t.Fatalf("deadline health = %d after %s", health.Code, time.Since(started))
	}
	start := perform(server.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.1:1", "")
	if start.Code != http.StatusGatewayTimeout {
		t.Fatalf("deadline start = %d, %s", start.Code, start.Body.String())
	}
	secondHealth := perform(server.Handler(), http.MethodGet, "/health", "", "", "127.0.0.1:1", "")
	if secondHealth.Code != http.StatusGatewayTimeout || strings.Contains(secondHealth.Body.String(), "server_busy") {
		t.Fatalf("deadline did not release concurrency slot: %d, %s", secondHealth.Code, secondHealth.Body.String())
	}
}

func TestTelemetryValidationAndRequestLimits(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	now := time.Now()
	server := New(dataStore, Options{
		Clock:           func() time.Time { return now },
		MaxRequestBytes: 256,
		TokenGenerator:  tokenQueue(tokenFixture(1)),
	})
	tests := []struct {
		name        string
		method      string
		contentType string
		body        string
		wantStatus  int
		wantCode    string
	}{
		{"method", http.MethodGet, "", "", http.StatusMethodNotAllowed, "method_not_allowed"},
		{"content type", http.MethodPost, "text/plain", validBody, http.StatusUnsupportedMediaType, "unsupported_media_type"},
		{"malformed", http.MethodPost, "application/json", "{", http.StatusBadRequest, "invalid_request"},
		{"unknown field", http.MethodPost, "application/json", `{"machineId":"` + testMachineLower + `","sessionId":"00112233445566778899aabbccddeeff","userAgent":"private"}`, http.StatusBadRequest, "invalid_request"},
		{"multiple values", http.MethodPost, "application/json", validBody + "{}", http.StatusBadRequest, "invalid_request"},
		{"raw machine", http.MethodPost, "application/json", `{"machineId":"DESKTOP-USER","sessionId":"00112233445566778899aabbccddeeff"}`, http.StatusBadRequest, "invalid_identity"},
		{"too large", http.MethodPost, "application/json", `{"machineId":"` + strings.Repeat("a", 300) + `","sessionId":"00112233445566778899aabbccddeeff"}`, http.StatusRequestEntityTooLarge, "request_too_large"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			response := perform(server.Handler(), test.method, startPath, test.body, test.contentType, "203.0.113.1:1", "")
			if response.Code != test.wantStatus || !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) {
				t.Fatalf("response = %d, %s", response.Code, response.Body.String())
			}
			if strings.Contains(response.Body.String(), "DESKTOP") || strings.Contains(response.Body.String(), "private") {
				t.Fatalf("response echoed client data: %s", response.Body.String())
			}
		})
	}
}

func TestTokenGenerationFailureIsGeneric(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	server := New(dataStore, Options{
		TokenGenerator: func() (string, sessiontoken.Digest, error) {
			return "", sessiontoken.Digest{}, errors.New("private entropy failure")
		},
	})
	response := perform(server.Handler(), http.MethodPost, startPath, validBody, "application/json", "203.0.113.1:1", "")
	if response.Code != http.StatusInternalServerError ||
		response.Body.String() != "{\"code\":\"token_generation_failed\"}\n" {
		t.Fatalf("token failure = %d, %q", response.Code, response.Body.String())
	}
}

func TestAdminPaginationIsBounded(t *testing.T) {
	dataStore := newTestStore(t)
	defer dataStore.Close()
	server := New(dataStore, Options{AdminToken: adminToken})
	for _, path := range []string{
		"/v1/admin/machines?limit=0",
		"/v1/admin/machines?limit=501",
		"/v1/admin/sessions?offset=-1",
		"/v1/admin/sessions?limit=not-a-number",
	} {
		response := perform(server.Handler(), http.MethodGet, path, "", "", "127.0.0.1:1", "Bearer "+adminToken)
		if response.Code != http.StatusBadRequest {
			t.Fatalf("%s status = %d", path, response.Code)
		}
	}
}

type tokenValue struct {
	raw    string
	digest sessiontoken.Digest
}

func tokenFixture(value byte) tokenValue {
	raw, digest, err := sessiontoken.GenerateFrom(bytes.NewReader(bytes.Repeat([]byte{value}, 32)))
	if err != nil {
		panic(err)
	}
	return tokenValue{raw: raw, digest: digest}
}

func tokenQueue(values ...tokenValue) TokenGenerator {
	var mutex sync.Mutex
	index := 0
	return func() (string, sessiontoken.Digest, error) {
		mutex.Lock()
		defer mutex.Unlock()
		if index >= len(values) {
			return "", sessiontoken.Digest{}, errors.New("test token queue exhausted")
		}
		value := values[index]
		index++
		return value.raw, value.digest, nil
	}
}

func testOptions(now *time.Time, generator TokenGenerator) Options {
	return Options{
		Clock:             func() time.Time { return *now },
		TokenGenerator:    generator,
		AdminToken:        adminToken,
		RequestsPerSecond: 1000,
		RequestBurst:      1000,
	}
}

type deadlineStore struct {
	DataStore
}

func (deadlineStore) Ping(ctx context.Context) error {
	<-ctx.Done()
	return ctx.Err()
}

func (deadlineStore) Start(
	ctx context.Context,
	_ string,
	_ string,
	_ sessiontoken.Digest,
	_ time.Time,
) (store.StartResult, error) {
	<-ctx.Done()
	return store.StartResult{}, ctx.Err()
}

func newTestStore(t *testing.T) *store.Store {
	t.Helper()
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "telemetry.db"))
	if err != nil {
		t.Fatal(err)
	}
	return dataStore
}

func newLimitedTestStore(t *testing.T, limits store.Limits) *store.Store {
	t.Helper()
	dataStore, err := store.OpenWithLimits(
		context.Background(), filepath.Join(t.TempDir(), "telemetry.db"), limits)
	if err != nil {
		t.Fatal(err)
	}
	return dataStore
}

func perform(
	handler http.Handler,
	method string,
	path string,
	body string,
	contentType string,
	remoteAddress string,
	authorization string,
) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, path, strings.NewReader(body))
	request.RemoteAddr = remoteAddress
	if contentType != "" {
		request.Header.Set("Content-Type", contentType)
	}
	if authorization != "" {
		request.Header.Set("Authorization", authorization)
	}
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}
