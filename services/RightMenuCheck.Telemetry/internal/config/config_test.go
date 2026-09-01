package config

import (
	"strings"
	"testing"
	"time"
)

func TestLoadRequiresAdminTokenByDefault(t *testing.T) {
	clearEnvironment(t)
	if _, err := Load(); err == nil {
		t.Fatal("Load() accepted the default configuration without an admin token")
	}
}

func TestLoadAllowsExplicitUnauthenticatedLoopbackTestMode(t *testing.T) {
	clearEnvironment(t)
	t.Setenv("RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN", "true")

	cfg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ListenAddress != defaultListenAddress || cfg.DatabasePath != defaultDatabasePath {
		t.Fatalf("unexpected defaults: %#v", cfg)
	}
	if cfg.SessionTimeout != 7*time.Minute || cfg.ClosedSessionTTL != 7*24*time.Hour ||
		cfg.HandlerTimeout != 5*time.Second {
		t.Fatalf("unexpected lifecycle defaults: %#v", cfg)
	}
	if cfg.RequestsPerSecond != 200 || cfg.RequestBurst != 400 ||
		cfg.NewSessionsPerMinute != 1000 || cfg.MaxDatabaseBytes != 512<<20 {
		t.Fatalf("unexpected capacity defaults: %#v", cfg)
	}
}

func TestLoadParsesProductionEnvironment(t *testing.T) {
	clearEnvironment(t)
	t.Setenv("RMC_TELEMETRY_LISTEN_ADDRESS", "[::1]:9443")
	t.Setenv("RMC_TELEMETRY_DATABASE_PATH", "state/service.db")
	t.Setenv("RMC_TELEMETRY_ADMIN_TOKEN", strings.Repeat("s", 32))
	t.Setenv("RMC_TELEMETRY_SESSION_TIMEOUT", "2m")
	t.Setenv("RMC_TELEMETRY_CLOSED_SESSION_TTL", "48h")
	t.Setenv("RMC_TELEMETRY_SWEEP_INTERVAL", "15s")
	t.Setenv("RMC_TELEMETRY_HANDLER_TIMEOUT", "3s")
	t.Setenv("RMC_TELEMETRY_SHUTDOWN_TIMEOUT", "5s")
	t.Setenv("RMC_TELEMETRY_MAX_REQUEST_BYTES", "8192")
	t.Setenv("RMC_TELEMETRY_MAX_CONCURRENT", "12")
	t.Setenv("RMC_TELEMETRY_REQUESTS_PER_SECOND", "25.5")
	t.Setenv("RMC_TELEMETRY_REQUEST_BURST", "50")
	t.Setenv("RMC_TELEMETRY_NEW_SESSIONS_PER_MINUTE", "75")
	t.Setenv("RMC_TELEMETRY_MAX_MACHINES", "1000")
	t.Setenv("RMC_TELEMETRY_MAX_ACTIVE_SESSIONS", "250")
	t.Setenv("RMC_TELEMETRY_MAX_DATABASE_BYTES", "10485760")

	cfg, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ListenAddress != "[::1]:9443" || cfg.SessionTimeout != 2*time.Minute ||
		cfg.ClosedSessionTTL != 48*time.Hour || cfg.HandlerTimeout != 3*time.Second ||
		cfg.RequestsPerSecond != 25.5 || cfg.NewSessionsPerMinute != 75 ||
		cfg.MaxMachines != 1000 || cfg.MaxActiveSessions != 250 ||
		cfg.MaxDatabaseBytes != 10<<20 {
		t.Fatalf("environment was not applied: %#v", cfg)
	}
}

func TestValidateAlwaysRejectsNonLoopbackListener(t *testing.T) {
	cfg := validConfig()
	for _, address := range []string{
		"0.0.0.0:8787",
		"192.0.2.1:8787",
		"localhost:8787",
		"[::]:8787",
	} {
		cfg.ListenAddress = address
		if err := cfg.Validate(); err == nil {
			t.Fatalf("Validate() accepted non-numeric-loopback address %q", address)
		}
	}
}

func TestValidateAdminAuthenticationModes(t *testing.T) {
	cfg := validConfig()
	cfg.AdminToken = ""
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate() accepted missing token without the test switch")
	}

	cfg.AllowUnauthenticatedLoopbackAdmin = true
	if err := cfg.Validate(); err != nil {
		t.Fatalf("Validate() rejected explicit loopback test mode: %v", err)
	}

	cfg.AllowUnauthenticatedLoopbackAdmin = false
	cfg.AdminToken = "short"
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate() accepted a short admin token")
	}
}

func TestLoadRejectsUnsafeValues(t *testing.T) {
	tests := map[string]string{
		"RMC_TELEMETRY_LISTEN_ADDRESS":                       ":8787",
		"RMC_TELEMETRY_ADMIN_TOKEN":                          "short",
		"RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN": "not-bool",
		"RMC_TELEMETRY_SESSION_TIMEOUT":                      "0s",
		"RMC_TELEMETRY_CLOSED_SESSION_TTL":                   "1s",
		"RMC_TELEMETRY_SWEEP_INTERVAL":                       "8m",
		"RMC_TELEMETRY_HANDLER_TIMEOUT":                      "31s",
		"RMC_TELEMETRY_SHUTDOWN_TIMEOUT":                     "-1s",
		"RMC_TELEMETRY_MAX_REQUEST_BYTES":                    "128",
		"RMC_TELEMETRY_MAX_CONCURRENT":                       "0",
		"RMC_TELEMETRY_REQUESTS_PER_SECOND":                  "0",
		"RMC_TELEMETRY_REQUEST_BURST":                        "0",
		"RMC_TELEMETRY_NEW_SESSIONS_PER_MINUTE":              "0",
		"RMC_TELEMETRY_MAX_MACHINES":                         "0",
		"RMC_TELEMETRY_MAX_ACTIVE_SESSIONS":                  "0",
		"RMC_TELEMETRY_MAX_DATABASE_BYTES":                   "1024",
	}
	for name, value := range tests {
		t.Run(name, func(t *testing.T) {
			clearEnvironment(t)
			t.Setenv("RMC_TELEMETRY_ADMIN_TOKEN", strings.Repeat("a", 32))
			t.Setenv(name, value)
			if _, err := Load(); err == nil {
				t.Fatalf("Load() accepted %s=%q", name, value)
			}
		})
	}
}

func validConfig() Config {
	return Config{
		ListenAddress:        "127.0.0.1:8787",
		DatabasePath:         "service.db",
		AdminToken:           strings.Repeat("a", 32),
		SessionTimeout:       time.Minute,
		ClosedSessionTTL:     time.Hour,
		SweepInterval:        time.Second,
		HandlerTimeout:       time.Second,
		ShutdownTimeout:      time.Second,
		MaxRequestBytes:      4096,
		MaxConcurrent:        1,
		RequestsPerSecond:    1,
		RequestBurst:         1,
		NewSessionsPerMinute: 1,
		MaxMachines:          1,
		MaxActiveSessions:    1,
		MaxDatabaseBytes:     1 << 20,
	}
}

func clearEnvironment(t *testing.T) {
	t.Helper()
	for _, name := range []string{
		"RMC_TELEMETRY_LISTEN_ADDRESS",
		"RMC_TELEMETRY_DATABASE_PATH",
		"RMC_TELEMETRY_ADMIN_TOKEN",
		"RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN",
		"RMC_TELEMETRY_SESSION_TIMEOUT",
		"RMC_TELEMETRY_CLOSED_SESSION_TTL",
		"RMC_TELEMETRY_SWEEP_INTERVAL",
		"RMC_TELEMETRY_HANDLER_TIMEOUT",
		"RMC_TELEMETRY_SHUTDOWN_TIMEOUT",
		"RMC_TELEMETRY_MAX_REQUEST_BYTES",
		"RMC_TELEMETRY_MAX_CONCURRENT",
		"RMC_TELEMETRY_REQUESTS_PER_SECOND",
		"RMC_TELEMETRY_REQUEST_BURST",
		"RMC_TELEMETRY_NEW_SESSIONS_PER_MINUTE",
		"RMC_TELEMETRY_MAX_MACHINES",
		"RMC_TELEMETRY_MAX_ACTIVE_SESSIONS",
		"RMC_TELEMETRY_MAX_DATABASE_BYTES",
	} {
		t.Setenv(name, "")
	}
}
