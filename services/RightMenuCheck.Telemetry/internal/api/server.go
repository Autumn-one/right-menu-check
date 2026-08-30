package api

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"

	"rightmenucheck.local/telemetry/internal/identity"
	"rightmenucheck.local/telemetry/internal/ratelimit"
	"rightmenucheck.local/telemetry/internal/sessiontoken"
	"rightmenucheck.local/telemetry/internal/store"
)

const (
	startPath     = "/v1/telemetry/start"
	resumePath    = "/v1/telemetry/resume"
	heartbeatPath = "/v1/telemetry/heartbeat"
	endPath       = "/v1/telemetry/end"
)

type DataStore interface {
	Ping(context.Context) error
	Start(context.Context, string, string, sessiontoken.Digest, time.Time) (store.StartResult, error)
	Resume(
		context.Context,
		string,
		string,
		string,
		sessiontoken.Digest,
		sessiontoken.Digest,
		time.Time,
	) (store.StartResult, error)
	Heartbeat(context.Context, string, string, sessiontoken.Digest, time.Time) error
	End(context.Context, string, string, sessiontoken.Digest, time.Time) error
	Summary(context.Context) (store.Summary, error)
	Machines(context.Context, int, int) ([]store.Machine, error)
	Sessions(context.Context, int, int, time.Time) ([]store.Session, error)
}

type Clock func() time.Time
type TokenGenerator func() (string, sessiontoken.Digest, error)

type Options struct {
	Clock                             Clock
	TokenGenerator                    TokenGenerator
	AdminToken                        string
	AllowUnauthenticatedLoopbackAdmin bool
	MaxRequestBytes                   int64
	MaxConcurrent                     int
	HandlerTimeout                    time.Duration
	RequestsPerSecond                 float64
	RequestBurst                      int
}

type Server struct {
	store                             DataStore
	clock                             Clock
	tokenGenerator                    TokenGenerator
	adminToken                        string
	allowUnauthenticatedLoopbackAdmin bool
	maxRequestBytes                   int64
	concurrency                       chan struct{}
	handlerTimeout                    time.Duration
	requestLimiter                    *ratelimit.Bucket
	handler                           http.Handler
}

type identityRequest struct {
	MachineID string `json:"machineId"`
	SessionID string `json:"sessionId"`
}

type resumeRequest struct {
	MachineID         string `json:"machineId"`
	PreviousSessionID string `json:"previousSessionId"`
	SessionID         string `json:"sessionId"`
}

type startResponse struct {
	StartupCount int       `json:"startupCount"`
	StartedAtUTC time.Time `json:"startedAtUtc"`
	SessionToken string    `json:"sessionToken"`
}

type errorResponse struct {
	Code string `json:"code"`
}

type summaryResponse struct {
	MachineCount         int64 `json:"machineCount"`
	StartupCount         int64 `json:"startupCount"`
	SessionCount         int64 `json:"sessionCount"`
	ActiveSessionCount   int64 `json:"activeSessionCount"`
	NormalSessionCount   int64 `json:"normalSessionCount"`
	AbnormalSessionCount int64 `json:"abnormalSessionCount"`
	TotalDurationMS      int64 `json:"totalDurationMilliseconds"`
}

type machineResponse struct {
	MachineID            string    `json:"machineId"`
	StartupCount         int       `json:"startupCount"`
	FirstStartedAtUTC    time.Time `json:"firstStartedAtUtc"`
	LastStartedAtUTC     time.Time `json:"lastStartedAtUtc"`
	TotalDurationMS      int64     `json:"totalDurationMilliseconds"`
	NormalSessionCount   int64     `json:"normalSessionCount"`
	AbnormalSessionCount int64     `json:"abnormalSessionCount"`
}

type sessionResponse struct {
	MachineID    string    `json:"machineId"`
	StartedAtUTC time.Time `json:"startedAtUtc"`
	DurationMS   int64     `json:"durationMilliseconds"`
	ExitKind     string    `json:"exitKind"`
}

type pageResponse[T any] struct {
	Items  []T `json:"items"`
	Limit  int `json:"limit"`
	Offset int `json:"offset"`
}

func New(dataStore DataStore, options Options) *Server {
	options = normalizeOptions(options)
	server := &Server{
		store:                             dataStore,
		clock:                             options.Clock,
		tokenGenerator:                    options.TokenGenerator,
		adminToken:                        options.AdminToken,
		allowUnauthenticatedLoopbackAdmin: options.AllowUnauthenticatedLoopbackAdmin,
		maxRequestBytes:                   options.MaxRequestBytes,
		concurrency:                       make(chan struct{}, options.MaxConcurrent),
		handlerTimeout:                    options.HandlerTimeout,
		requestLimiter: ratelimit.New(
			options.RequestsPerSecond,
			options.RequestBurst,
			options.Clock(),
		),
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/health", server.health)
	mux.HandleFunc(startPath, server.start)
	mux.HandleFunc(resumePath, server.resume)
	mux.HandleFunc(heartbeatPath, server.heartbeat)
	mux.HandleFunc(endPath, server.end)
	mux.Handle("/v1/admin/summary", server.adminOnly(http.HandlerFunc(server.summary)))
	mux.Handle("/v1/admin/machines", server.adminOnly(http.HandlerFunc(server.machines)))
	mux.Handle("/v1/admin/sessions", server.adminOnly(http.HandlerFunc(server.sessions)))
	server.handler = securityHeaders(
		server.withDeadline(
			server.limitRate(
				server.limitConcurrency(mux),
			),
		),
	)
	return server
}

func normalizeOptions(options Options) Options {
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.TokenGenerator == nil {
		options.TokenGenerator = sessiontoken.Generate
	}
	if options.MaxRequestBytes == 0 {
		options.MaxRequestBytes = 4096
	}
	if options.MaxConcurrent == 0 {
		options.MaxConcurrent = 64
	}
	if options.HandlerTimeout == 0 {
		options.HandlerTimeout = 5 * time.Second
	}
	if options.RequestsPerSecond == 0 {
		options.RequestsPerSecond = 200
	}
	if options.RequestBurst == 0 {
		options.RequestBurst = 400
	}
	return options
}

func (s *Server) Handler() http.Handler {
	return s.handler
}

func (s *Server) withDeadline(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		ctx, cancel := context.WithTimeout(request.Context(), s.handlerTimeout)
		defer cancel()
		next.ServeHTTP(response, request.WithContext(ctx))
	})
}

func (s *Server) limitRate(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		if !s.requestLimiter.Allow(s.clock()) {
			response.Header().Set("Retry-After", "1")
			writeError(response, http.StatusTooManyRequests, "rate_limited")
			return
		}
		next.ServeHTTP(response, request)
	})
}

func (s *Server) limitConcurrency(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		select {
		case s.concurrency <- struct{}{}:
			defer func() { <-s.concurrency }()
			next.ServeHTTP(response, request)
		default:
			writeError(response, http.StatusServiceUnavailable, "server_busy")
		}
	})
}

func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Cache-Control", "no-store")
		response.Header().Set("X-Content-Type-Options", "nosniff")
		next.ServeHTTP(response, request)
	})
}

func (s *Server) health(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		methodNotAllowed(response, http.MethodGet)
		return
	}
	if err := s.store.Ping(request.Context()); err != nil {
		if errors.Is(err, context.DeadlineExceeded) {
			writeError(response, http.StatusGatewayTimeout, "request_timeout")
		} else {
			writeError(response, http.StatusServiceUnavailable, "storage_unavailable")
		}
		return
	}
	writeJSON(response, http.StatusOK, struct {
		Status string `json:"status"`
	}{Status: "ok"})
}

func (s *Server) start(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodPost {
		methodNotAllowed(response, http.MethodPost)
		return
	}
	machineID, sessionID, ok := s.readIdentity(response, request)
	if !ok {
		return
	}
	token, tokenHash, err := s.tokenGenerator()
	if err != nil {
		writeError(response, http.StatusInternalServerError, "token_generation_failed")
		return
	}
	result, err := s.store.Start(
		request.Context(), machineID, sessionID, tokenHash, s.clock().UTC())
	if err != nil {
		s.writeStoreError(response, err)
		return
	}
	writeJSON(response, http.StatusOK, startResponse{
		StartupCount: result.StartupCount,
		StartedAtUTC: result.StartedAt.UTC(),
		SessionToken: token,
	})
}

func (s *Server) resume(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodPost {
		methodNotAllowed(response, http.MethodPost)
		return
	}
	previousTokenHash, ok := readSessionAuthorization(response, request)
	if !ok {
		return
	}
	machineID, previousSessionID, sessionID, ok := s.readResume(response, request)
	if !ok {
		return
	}
	token, tokenHash, err := s.tokenGenerator()
	if err != nil {
		writeError(response, http.StatusInternalServerError, "token_generation_failed")
		return
	}
	result, err := s.store.Resume(
		request.Context(),
		machineID,
		previousSessionID,
		sessionID,
		previousTokenHash,
		tokenHash,
		s.clock().UTC(),
	)
	if err != nil {
		s.writeStoreError(response, err)
		return
	}
	writeJSON(response, http.StatusOK, startResponse{
		StartupCount: result.StartupCount,
		StartedAtUTC: result.StartedAt.UTC(),
		SessionToken: token,
	})
}

func (s *Server) heartbeat(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodPost {
		methodNotAllowed(response, http.MethodPost)
		return
	}
	tokenHash, ok := readSessionAuthorization(response, request)
	if !ok {
		return
	}
	machineID, sessionID, ok := s.readIdentity(response, request)
	if !ok {
		return
	}
	if err := s.store.Heartbeat(
		request.Context(), machineID, sessionID, tokenHash, s.clock().UTC()); err != nil {
		s.writeStoreError(response, err)
		return
	}
	response.WriteHeader(http.StatusNoContent)
}

func (s *Server) end(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodPost {
		methodNotAllowed(response, http.MethodPost)
		return
	}
	tokenHash, ok := readSessionAuthorization(response, request)
	if !ok {
		return
	}
	machineID, sessionID, ok := s.readIdentity(response, request)
	if !ok {
		return
	}
	if err := s.store.End(
		request.Context(), machineID, sessionID, tokenHash, s.clock().UTC()); err != nil {
		s.writeStoreError(response, err)
		return
	}
	response.WriteHeader(http.StatusNoContent)
}

func (s *Server) readIdentity(
	response http.ResponseWriter,
	request *http.Request,
) (string, string, bool) {
	var payload identityRequest
	if !s.decodeJSON(response, request, &payload) {
		return "", "", false
	}
	machineID, validMachine := identity.MachineID(payload.MachineID)
	sessionID, validSession := identity.SessionID(payload.SessionID)
	if !validMachine || !validSession {
		writeError(response, http.StatusBadRequest, "invalid_identity")
		return "", "", false
	}
	return machineID, sessionID, true
}

func (s *Server) readResume(
	response http.ResponseWriter,
	request *http.Request,
) (string, string, string, bool) {
	var payload resumeRequest
	if !s.decodeJSON(response, request, &payload) {
		return "", "", "", false
	}
	machineID, validMachine := identity.MachineID(payload.MachineID)
	previousSessionID, validPrevious := identity.SessionID(payload.PreviousSessionID)
	sessionID, validSession := identity.SessionID(payload.SessionID)
	if !validMachine || !validPrevious || !validSession || previousSessionID == sessionID {
		writeError(response, http.StatusBadRequest, "invalid_identity")
		return "", "", "", false
	}
	return machineID, previousSessionID, sessionID, true
}

func (s *Server) decodeJSON(response http.ResponseWriter, request *http.Request, payload any) bool {
	mediaType, _, err := mime.ParseMediaType(request.Header.Get("Content-Type"))
	if err != nil || !strings.EqualFold(mediaType, "application/json") {
		writeError(response, http.StatusUnsupportedMediaType, "unsupported_media_type")
		return false
	}

	request.Body = http.MaxBytesReader(response, request.Body, s.maxRequestBytes)
	decoder := json.NewDecoder(request.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(payload); err != nil {
		s.writeJSONDecodeError(response, err)
		return false
	}
	if err := ensureEndOfJSON(decoder); err != nil {
		s.writeJSONDecodeError(response, err)
		return false
	}
	return true
}

func (s *Server) writeJSONDecodeError(response http.ResponseWriter, err error) {
	var maxBytesError *http.MaxBytesError
	if errors.As(err, &maxBytesError) {
		writeError(response, http.StatusRequestEntityTooLarge, "request_too_large")
	} else {
		writeError(response, http.StatusBadRequest, "invalid_request")
	}
}

func ensureEndOfJSON(decoder *json.Decoder) error {
	var extra any
	if err := decoder.Decode(&extra); !errors.Is(err, io.EOF) {
		if err == nil {
			return errors.New("multiple JSON values")
		}
		return err
	}
	return nil
}

func readSessionAuthorization(
	response http.ResponseWriter,
	request *http.Request,
) (sessiontoken.Digest, bool) {
	digest, ok := sessiontoken.DigestAuthorization(request.Header.Get("Authorization"))
	if !ok {
		writeUnauthorized(response, "invalid_session_token")
		return sessiontoken.Digest{}, false
	}
	return digest, true
}

func (s *Server) writeStoreError(response http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, context.DeadlineExceeded), errors.Is(err, context.Canceled):
		writeError(response, http.StatusGatewayTimeout, "request_timeout")
	case errors.Is(err, store.ErrInvalidSessionToken):
		writeUnauthorized(response, "invalid_session_token")
	case errors.Is(err, store.ErrSessionNotFound):
		writeError(response, http.StatusNotFound, "session_not_found")
	case errors.Is(err, store.ErrSessionConflict):
		writeError(response, http.StatusConflict, "session_conflict")
	case errors.Is(err, store.ErrSessionEnded):
		writeError(response, http.StatusConflict, "session_ended")
	case errors.Is(err, store.ErrNewSessionRateLimit):
		response.Header().Set("Retry-After", "60")
		writeError(response, http.StatusTooManyRequests, "new_session_rate_limited")
	case errors.Is(err, store.ErrCapacity):
		writeError(response, http.StatusInsufficientStorage, "capacity_exceeded")
	default:
		writeError(response, http.StatusInternalServerError, "storage_error")
	}
}

func (s *Server) adminOnly(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		authorized := s.validAdminToken(request.Header.Get("Authorization"))
		if s.adminToken == "" && s.allowUnauthenticatedLoopbackAdmin {
			authorized = isLoopback(request.RemoteAddr)
		}
		if !authorized {
			writeUnauthorized(response, "unauthorized")
			return
		}
		next.ServeHTTP(response, request)
	})
}

func (s *Server) validAdminToken(header string) bool {
	if s.adminToken == "" {
		return false
	}
	provided, ok := bearerValue(header)
	return ok && len(provided) == len(s.adminToken) &&
		subtle.ConstantTimeCompare([]byte(provided), []byte(s.adminToken)) == 1
}

func bearerValue(header string) (string, bool) {
	scheme, value, ok := strings.Cut(header, " ")
	return value, ok && strings.EqualFold(scheme, "Bearer") && value != "" &&
		!strings.ContainsAny(value, " \t\r\n")
}

func isLoopback(remoteAddress string) bool {
	host, _, err := net.SplitHostPort(remoteAddress)
	if err != nil {
		return false
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func (s *Server) summary(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		methodNotAllowed(response, http.MethodGet)
		return
	}
	result, err := s.store.Summary(request.Context())
	if err != nil {
		s.writeStoreError(response, err)
		return
	}
	writeJSON(response, http.StatusOK, summaryResponse{
		MachineCount:         result.MachineCount,
		StartupCount:         result.StartupCount,
		SessionCount:         result.SessionCount,
		ActiveSessionCount:   result.ActiveSessionCount,
		NormalSessionCount:   result.NormalSessionCount,
		AbnormalSessionCount: result.AbnormalSessionCount,
		TotalDurationMS:      result.TotalDurationMS,
	})
}

func (s *Server) machines(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		methodNotAllowed(response, http.MethodGet)
		return
	}
	limit, offset, ok := pagination(response, request)
	if !ok {
		return
	}
	rows, err := s.store.Machines(request.Context(), limit, offset)
	if err != nil {
		s.writeStoreError(response, err)
		return
	}
	items := make([]machineResponse, 0, len(rows))
	for _, row := range rows {
		items = append(items, machineResponse{
			MachineID:            row.MachineID,
			StartupCount:         row.StartupCount,
			FirstStartedAtUTC:    row.FirstStartedAt.UTC(),
			LastStartedAtUTC:     row.LastStartedAt.UTC(),
			TotalDurationMS:      row.TotalDurationMS,
			NormalSessionCount:   row.NormalSessionCount,
			AbnormalSessionCount: row.AbnormalSessionCount,
		})
	}
	writeJSON(response, http.StatusOK, pageResponse[machineResponse]{
		Items: items, Limit: limit, Offset: offset,
	})
}

func (s *Server) sessions(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		methodNotAllowed(response, http.MethodGet)
		return
	}
	limit, offset, ok := pagination(response, request)
	if !ok {
		return
	}
	rows, err := s.store.Sessions(request.Context(), limit, offset, s.clock().UTC())
	if err != nil {
		s.writeStoreError(response, err)
		return
	}
	items := make([]sessionResponse, 0, len(rows))
	for _, row := range rows {
		items = append(items, sessionResponse{
			MachineID:    row.MachineID,
			StartedAtUTC: row.StartedAt.UTC(),
			DurationMS:   row.DurationMS,
			ExitKind:     row.ExitKind,
		})
	}
	writeJSON(response, http.StatusOK, pageResponse[sessionResponse]{
		Items: items, Limit: limit, Offset: offset,
	})
}

func pagination(response http.ResponseWriter, request *http.Request) (int, int, bool) {
	limit, ok := queryInteger(request, "limit", 100, 1, 500)
	if !ok {
		writeError(response, http.StatusBadRequest, "invalid_pagination")
		return 0, 0, false
	}
	offset, ok := queryInteger(request, "offset", 0, 0, 1_000_000_000)
	if !ok {
		writeError(response, http.StatusBadRequest, "invalid_pagination")
		return 0, 0, false
	}
	return limit, offset, true
}

func queryInteger(request *http.Request, name string, fallback, minimum, maximum int) (int, bool) {
	value := request.URL.Query().Get(name)
	if value == "" {
		return fallback, true
	}
	parsed, err := strconv.Atoi(value)
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, false
	}
	return parsed, true
}

func methodNotAllowed(response http.ResponseWriter, allowed string) {
	response.Header().Set("Allow", allowed)
	writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
}

func writeUnauthorized(response http.ResponseWriter, code string) {
	response.Header().Set("WWW-Authenticate", "Bearer")
	writeError(response, http.StatusUnauthorized, code)
}

func writeError(response http.ResponseWriter, status int, code string) {
	writeJSON(response, status, errorResponse{Code: code})
}

func writeJSON(response http.ResponseWriter, status int, value any) {
	response.Header().Set("Content-Type", "application/json; charset=utf-8")
	response.WriteHeader(status)
	_ = json.NewEncoder(response).Encode(value)
}
