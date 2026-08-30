package config

import (
	"errors"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

const (
	defaultListenAddress = "127.0.0.1:8787"
	defaultDatabasePath  = "data/telemetry.db"
)

type Config struct {
	ListenAddress                     string
	DatabasePath                      string
	AdminToken                        string
	AllowUnauthenticatedLoopbackAdmin bool
	SessionTimeout                    time.Duration
	ClosedSessionTTL                  time.Duration
	SweepInterval                     time.Duration
	HandlerTimeout                    time.Duration
	ShutdownTimeout                   time.Duration
	MaxRequestBytes                   int64
	MaxConcurrent                     int
	RequestsPerSecond                 float64
	RequestBurst                      int
	NewSessionsPerMinute              int64
	MaxMachines                       int64
	MaxActiveSessions                 int64
	MaxDatabaseBytes                  int64
}

func Load() (Config, error) {
	cfg := Config{
		ListenAddress:        valueOrDefault("RMC_TELEMETRY_LISTEN_ADDRESS", defaultListenAddress),
		DatabasePath:         valueOrDefault("RMC_TELEMETRY_DATABASE_PATH", defaultDatabasePath),
		AdminToken:           os.Getenv("RMC_TELEMETRY_ADMIN_TOKEN"),
		SessionTimeout:       3 * time.Minute,
		ClosedSessionTTL:     7 * 24 * time.Hour,
		SweepInterval:        30 * time.Second,
		HandlerTimeout:       5 * time.Second,
		ShutdownTimeout:      10 * time.Second,
		MaxRequestBytes:      4096,
		MaxConcurrent:        64,
		RequestsPerSecond:    200,
		RequestBurst:         400,
		NewSessionsPerMinute: 1000,
		MaxMachines:          1_000_000,
		MaxActiveSessions:    100_000,
		MaxDatabaseBytes:     512 << 20,
	}

	var err error
	if cfg.AllowUnauthenticatedLoopbackAdmin, err = boolFromEnv(
		"RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN", false); err != nil {
		return Config{}, err
	}
	if cfg.SessionTimeout, err = durationFromEnv("RMC_TELEMETRY_SESSION_TIMEOUT", cfg.SessionTimeout); err != nil {
		return Config{}, err
	}
	if cfg.ClosedSessionTTL, err = durationFromEnv("RMC_TELEMETRY_CLOSED_SESSION_TTL", cfg.ClosedSessionTTL); err != nil {
		return Config{}, err
	}
	if cfg.SweepInterval, err = durationFromEnv("RMC_TELEMETRY_SWEEP_INTERVAL", cfg.SweepInterval); err != nil {
		return Config{}, err
	}
	if cfg.HandlerTimeout, err = durationFromEnv("RMC_TELEMETRY_HANDLER_TIMEOUT", cfg.HandlerTimeout); err != nil {
		return Config{}, err
	}
	if cfg.ShutdownTimeout, err = durationFromEnv("RMC_TELEMETRY_SHUTDOWN_TIMEOUT", cfg.ShutdownTimeout); err != nil {
		return Config{}, err
	}
	if cfg.MaxRequestBytes, err = int64FromEnv("RMC_TELEMETRY_MAX_REQUEST_BYTES", cfg.MaxRequestBytes); err != nil {
		return Config{}, err
	}
	if cfg.MaxConcurrent, err = intFromEnv("RMC_TELEMETRY_MAX_CONCURRENT", cfg.MaxConcurrent); err != nil {
		return Config{}, err
	}
	if cfg.RequestsPerSecond, err = float64FromEnv("RMC_TELEMETRY_REQUESTS_PER_SECOND", cfg.RequestsPerSecond); err != nil {
		return Config{}, err
	}
	if cfg.RequestBurst, err = intFromEnv("RMC_TELEMETRY_REQUEST_BURST", cfg.RequestBurst); err != nil {
		return Config{}, err
	}
	if cfg.NewSessionsPerMinute, err = int64FromEnv("RMC_TELEMETRY_NEW_SESSIONS_PER_MINUTE", cfg.NewSessionsPerMinute); err != nil {
		return Config{}, err
	}
	if cfg.MaxMachines, err = int64FromEnv("RMC_TELEMETRY_MAX_MACHINES", cfg.MaxMachines); err != nil {
		return Config{}, err
	}
	if cfg.MaxActiveSessions, err = int64FromEnv("RMC_TELEMETRY_MAX_ACTIVE_SESSIONS", cfg.MaxActiveSessions); err != nil {
		return Config{}, err
	}
	if cfg.MaxDatabaseBytes, err = int64FromEnv("RMC_TELEMETRY_MAX_DATABASE_BYTES", cfg.MaxDatabaseBytes); err != nil {
		return Config{}, err
	}

	if err := cfg.Validate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
}

func (cfg Config) Validate() error {
	if strings.TrimSpace(cfg.DatabasePath) == "" {
		return errors.New("database path cannot be empty")
	}

	host, port, err := net.SplitHostPort(cfg.ListenAddress)
	if err != nil {
		return fmt.Errorf("listen address must include an explicit host and port: %w", err)
	}
	ip := net.ParseIP(host)
	if ip == nil || !ip.IsLoopback() {
		return errors.New("listen address must use a numeric loopback IP address")
	}
	parsedPort, err := strconv.ParseUint(port, 10, 16)
	if err != nil || parsedPort == 0 {
		return errors.New("listen address must use a port from 1 to 65535")
	}

	if cfg.AdminToken == "" && !cfg.AllowUnauthenticatedLoopbackAdmin {
		return errors.New("admin token is required unless unauthenticated loopback admin is explicitly enabled")
	}
	if cfg.AdminToken != "" && len(cfg.AdminToken) < 32 {
		return errors.New("admin token must contain at least 32 characters")
	}
	if cfg.AdminToken != "" && (strings.TrimSpace(cfg.AdminToken) != cfg.AdminToken ||
		strings.ContainsAny(cfg.AdminToken, " \t\r\n")) {
		return errors.New("admin token cannot contain whitespace")
	}
	if cfg.SessionTimeout <= 0 {
		return errors.New("session timeout must be positive")
	}
	if cfg.ClosedSessionTTL < cfg.SessionTimeout {
		return errors.New("closed session TTL must be at least the session timeout")
	}
	if cfg.SweepInterval <= 0 || cfg.SweepInterval > cfg.SessionTimeout {
		return errors.New("sweep interval must be positive and no greater than the session timeout")
	}
	if cfg.HandlerTimeout < 100*time.Millisecond || cfg.HandlerTimeout > 30*time.Second {
		return errors.New("handler timeout must be between 100 milliseconds and 30 seconds")
	}
	if cfg.ShutdownTimeout <= 0 {
		return errors.New("shutdown timeout must be positive")
	}
	if cfg.MaxRequestBytes < 256 || cfg.MaxRequestBytes > 1<<20 {
		return errors.New("maximum request size must be between 256 bytes and 1 MiB")
	}
	if cfg.MaxConcurrent < 1 || cfg.MaxConcurrent > 4096 {
		return errors.New("maximum concurrency must be between 1 and 4096")
	}
	if cfg.RequestsPerSecond <= 0 || cfg.RequestsPerSecond > 1_000_000 {
		return errors.New("requests per second must be greater than zero and at most 1000000")
	}
	if cfg.RequestBurst < 1 || cfg.RequestBurst > 1_000_000 {
		return errors.New("request burst must be between 1 and 1000000")
	}
	if cfg.NewSessionsPerMinute < 1 || cfg.NewSessionsPerMinute > 10_000_000 {
		return errors.New("new sessions per minute must be between 1 and 10000000")
	}
	if cfg.MaxMachines < 1 || cfg.MaxMachines > 100_000_000 {
		return errors.New("maximum machines must be between 1 and 100000000")
	}
	if cfg.MaxActiveSessions < 1 || cfg.MaxActiveSessions > 100_000_000 {
		return errors.New("maximum active sessions must be between 1 and 100000000")
	}
	if cfg.MaxDatabaseBytes < 1<<20 || cfg.MaxDatabaseBytes > 1<<40 {
		return errors.New("maximum database size must be between 1 MiB and 1 TiB")
	}
	return nil
}

func valueOrDefault(name, fallback string) string {
	if value := strings.TrimSpace(os.Getenv(name)); value != "" {
		return value
	}
	return fallback
}

func boolFromEnv(name string, fallback bool) (bool, error) {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback, nil
	}
	parsed, err := strconv.ParseBool(value)
	if err != nil {
		return false, fmt.Errorf("%s is invalid: %w", name, err)
	}
	return parsed, nil
}

func durationFromEnv(name string, fallback time.Duration) (time.Duration, error) {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback, nil
	}
	parsed, err := time.ParseDuration(value)
	if err != nil {
		return 0, fmt.Errorf("%s is invalid: %w", name, err)
	}
	return parsed, nil
}

func int64FromEnv(name string, fallback int64) (int64, error) {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback, nil
	}
	parsed, err := strconv.ParseInt(value, 10, 64)
	if err != nil {
		return 0, fmt.Errorf("%s is invalid: %w", name, err)
	}
	return parsed, nil
}

func intFromEnv(name string, fallback int) (int, error) {
	value, err := int64FromEnv(name, int64(fallback))
	if err != nil {
		return 0, err
	}
	if int64(int(value)) != value {
		return 0, fmt.Errorf("%s is outside the supported integer range", name)
	}
	return int(value), nil
}

func float64FromEnv(name string, fallback float64) (float64, error) {
	value := strings.TrimSpace(os.Getenv(name))
	if value == "" {
		return fallback, nil
	}
	parsed, err := strconv.ParseFloat(value, 64)
	if err != nil {
		return 0, fmt.Errorf("%s is invalid: %w", name, err)
	}
	return parsed, nil
}
