package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"rightmenucheck.local/telemetry/internal/sessiontoken"

	"modernc.org/sqlite"
)

const currentSchemaVersion = 2

var (
	ErrSessionNotFound     = errors.New("session not found")
	ErrSessionConflict     = errors.New("session conflict")
	ErrSessionEnded        = errors.New("session is already ended")
	ErrInvalidSessionToken = errors.New("invalid session token")
	ErrNewSessionRateLimit = errors.New("new session rate limit reached")
	ErrCapacity            = errors.New("telemetry capacity reached")
)

type Limits struct {
	NewSessionsPerMinute int64
	MaxMachines          int64
	MaxActiveSessions    int64
	MaxDatabaseBytes     int64
}

func DefaultLimits() Limits {
	return Limits{
		NewSessionsPerMinute: 1000,
		MaxMachines:          1_000_000,
		MaxActiveSessions:    100_000,
		MaxDatabaseBytes:     512 << 20,
	}
}

type Store struct {
	db     *sql.DB
	limits Limits
}

type StartResult struct {
	StartupCount int
	StartedAt    time.Time
}

type Summary struct {
	MachineCount         int64
	StartupCount         int64
	SessionCount         int64
	ActiveSessionCount   int64
	ActiveMachineCount   int64
	NormalSessionCount   int64
	AbnormalSessionCount int64
	TotalDurationMS      int64
}

type Machine struct {
	MachineID            string
	StartupCount         int
	FirstStartedAt       time.Time
	LastStartedAt        time.Time
	TotalDurationMS      int64
	NormalSessionCount   int64
	AbnormalSessionCount int64
	ActiveSessionCount   int64
	LastSeenAt           time.Time
}

type Session struct {
	MachineID  string
	StartedAt  time.Time
	LastSeenAt time.Time
	EndedAt    *time.Time
	DurationMS int64
	ExitKind   string
}

const schemaV2 = `
CREATE TABLE IF NOT EXISTS machines (
    machine_id TEXT PRIMARY KEY,
    startup_count INTEGER NOT NULL CHECK (startup_count >= 1),
    first_started_at_ms INTEGER NOT NULL,
    last_started_at_ms INTEGER NOT NULL,
    total_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (total_duration_ms >= 0),
    normal_session_count INTEGER NOT NULL DEFAULT 0 CHECK (normal_session_count >= 0),
    abnormal_session_count INTEGER NOT NULL DEFAULT 0 CHECK (abnormal_session_count >= 0),
    CHECK (length(machine_id) = 64 AND machine_id NOT GLOB '*[^0-9a-f]*'),
    CHECK (last_started_at_ms >= first_started_at_ms)
) STRICT;

CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id) ON DELETE RESTRICT,
    startup_count INTEGER NOT NULL CHECK (startup_count >= 1),
    token_hash BLOB CHECK (token_hash IS NULL OR length(token_hash) = 32),
    started_at_ms INTEGER NOT NULL,
    last_seen_at_ms INTEGER NOT NULL,
    ended_at_ms INTEGER,
    duration_ms INTEGER CHECK (duration_ms IS NULL OR duration_ms >= 0),
    exit_kind TEXT CHECK (exit_kind IS NULL OR exit_kind IN ('normal', 'abnormal')),
    CHECK (length(session_id) = 32 AND session_id NOT GLOB '*[^0-9a-f]*'),
    CHECK (last_seen_at_ms >= started_at_ms),
    CHECK (
        (ended_at_ms IS NULL AND duration_ms IS NULL AND exit_kind IS NULL) OR
        (ended_at_ms IS NOT NULL AND duration_ms IS NOT NULL AND exit_kind IS NOT NULL AND
         ended_at_ms >= last_seen_at_ms AND duration_ms = ended_at_ms - started_at_ms)
    )
) STRICT;

CREATE TABLE IF NOT EXISTS admission_windows (
    window_started_at_ms INTEGER PRIMARY KEY,
    session_count INTEGER NOT NULL CHECK (session_count >= 0)
) STRICT;

CREATE INDEX IF NOT EXISTS sessions_open_last_seen_idx
ON sessions(last_seen_at_ms)
WHERE ended_at_ms IS NULL;

CREATE INDEX IF NOT EXISTS sessions_machine_started_idx
ON sessions(machine_id, started_at_ms DESC);

CREATE INDEX IF NOT EXISTS sessions_closed_ended_idx
ON sessions(ended_at_ms)
WHERE ended_at_ms IS NOT NULL;
`

func Open(ctx context.Context, path string) (*Store, error) {
	return OpenWithLimits(ctx, path, DefaultLimits())
}

func OpenWithLimits(ctx context.Context, path string, limits Limits) (*Store, error) {
	if path == "" {
		return nil, errors.New("database path cannot be empty")
	}
	if err := validateLimits(limits); err != nil {
		return nil, err
	}
	if path != ":memory:" {
		directory := filepath.Dir(path)
		if err := os.MkdirAll(directory, 0o700); err != nil {
			return nil, fmt.Errorf("create database directory: %w", err)
		}
		if err := secureStorageDirectory(directory); err != nil {
			return nil, fmt.Errorf("restrict database directory permissions: %w", err)
		}
		if err := secureStorageFiles(path); err != nil {
			return nil, fmt.Errorf("restrict existing database permissions: %w", err)
		}
	}

	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, fmt.Errorf("open database: %w", err)
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)

	closeOnError := func(operation string, operationErr error) (*Store, error) {
		_ = db.Close()
		return nil, fmt.Errorf("%s: %w", operation, normalizeSQLiteError(operationErr))
	}

	for _, statement := range []string{
		"PRAGMA foreign_keys = ON",
		"PRAGMA journal_mode = WAL",
		"PRAGMA synchronous = FULL",
		"PRAGMA secure_delete = ON",
		"PRAGMA busy_timeout = 5000",
	} {
		if _, err := db.ExecContext(ctx, statement); err != nil {
			return closeOnError("configure database", err)
		}
	}
	if err := applyDatabaseSizeLimit(ctx, db, limits.MaxDatabaseBytes); err != nil {
		return closeOnError("apply database size limit", err)
	}
	if err := migrate(ctx, db); err != nil {
		return closeOnError("migrate database", err)
	}

	var integrity string
	if err := db.QueryRowContext(ctx, "PRAGMA quick_check").Scan(&integrity); err != nil {
		return closeOnError("check database integrity", err)
	}
	if integrity != "ok" {
		return closeOnError("check database integrity", fmt.Errorf("result was %q", integrity))
	}
	if path != ":memory:" {
		if err := secureStorageFiles(path); err != nil {
			return closeOnError("restrict database permissions", err)
		}
	}

	return &Store{db: db, limits: limits}, nil
}

func secureStorageFiles(databasePath string) error {
	for _, databaseFile := range []string{
		databasePath,
		databasePath + "-wal",
		databasePath + "-shm",
	} {
		if err := secureStorageFile(databaseFile); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	return nil
}

func (s *Store) Close() error {
	return s.db.Close()
}

func (s *Store) Ping(ctx context.Context) error {
	return s.db.PingContext(ctx)
}

func validateLimits(limits Limits) error {
	if limits.NewSessionsPerMinute < 1 || limits.MaxMachines < 1 ||
		limits.MaxActiveSessions < 1 || limits.MaxDatabaseBytes < 64<<10 {
		return errors.New("all store limits must be positive and database capacity must be at least 64 KiB")
	}
	return nil
}

func applyDatabaseSizeLimit(ctx context.Context, db *sql.DB, maxBytes int64) error {
	var pageSize, pageCount int64
	if err := db.QueryRowContext(ctx, "PRAGMA page_size").Scan(&pageSize); err != nil {
		return err
	}
	if err := db.QueryRowContext(ctx, "PRAGMA page_count").Scan(&pageCount); err != nil {
		return err
	}
	maxPages := maxBytes / pageSize
	if maxPages < 1 || pageCount > maxPages {
		return ErrCapacity
	}
	var appliedMaxPages int64
	if err := db.QueryRowContext(
		ctx, fmt.Sprintf("PRAGMA max_page_count = %d", maxPages)).Scan(&appliedMaxPages); err != nil {
		return err
	}
	if appliedMaxPages > maxPages {
		return ErrCapacity
	}
	checkpointPages := min(maxPages/16, 1000)
	if checkpointPages < 1 {
		checkpointPages = 1
	}
	if _, err := db.ExecContext(ctx, fmt.Sprintf("PRAGMA wal_autocheckpoint = %d", checkpointPages)); err != nil {
		return err
	}
	if _, err := db.ExecContext(ctx, fmt.Sprintf("PRAGMA journal_size_limit = %d", maxBytes/4)); err != nil {
		return err
	}
	return nil
}

func migrate(ctx context.Context, db *sql.DB) error {
	var version int
	if err := db.QueryRowContext(ctx, "PRAGMA user_version").Scan(&version); err != nil {
		return err
	}
	if version > currentSchemaVersion {
		return fmt.Errorf("unsupported schema version %d", version)
	}
	if version == currentSchemaVersion {
		_, err := db.ExecContext(ctx, schemaV2)
		return err
	}

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()

	switch version {
	case 0:
		if _, err := tx.ExecContext(ctx, schemaV2); err != nil {
			return err
		}
	case 1:
		if err := migrateV1ToV2(ctx, tx); err != nil {
			return err
		}
	}
	if _, err := tx.ExecContext(ctx, fmt.Sprintf("PRAGMA user_version = %d", currentSchemaVersion)); err != nil {
		return err
	}
	return tx.Commit()
}

func migrateV1ToV2(ctx context.Context, tx *sql.Tx) error {
	statements := []string{
		"ALTER TABLE machines ADD COLUMN total_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (total_duration_ms >= 0)",
		"ALTER TABLE machines ADD COLUMN normal_session_count INTEGER NOT NULL DEFAULT 0 CHECK (normal_session_count >= 0)",
		"ALTER TABLE machines ADD COLUMN abnormal_session_count INTEGER NOT NULL DEFAULT 0 CHECK (abnormal_session_count >= 0)",
		"ALTER TABLE sessions ADD COLUMN token_hash BLOB CHECK (token_hash IS NULL OR length(token_hash) = 32)",
		`UPDATE sessions
SET ended_at_ms = last_seen_at_ms,
    duration_ms = MAX(0, last_seen_at_ms - started_at_ms),
    exit_kind = 'abnormal'
WHERE ended_at_ms IS NULL`,
		`UPDATE machines
SET total_duration_ms = COALESCE((
        SELECT SUM(duration_ms) FROM sessions WHERE sessions.machine_id = machines.machine_id
    ), 0),
    normal_session_count = COALESCE((
        SELECT COUNT(*) FROM sessions
        WHERE sessions.machine_id = machines.machine_id AND exit_kind = 'normal'
    ), 0),
    abnormal_session_count = COALESCE((
        SELECT COUNT(*) FROM sessions
        WHERE sessions.machine_id = machines.machine_id AND exit_kind = 'abnormal'
    ), 0)`,
		schemaV2,
	}
	for _, statement := range statements {
		if _, err := tx.ExecContext(ctx, statement); err != nil {
			return err
		}
	}
	return nil
}

func normalizeSQLiteError(err error) error {
	var sqliteError *sqlite.Error
	if errors.As(err, &sqliteError) && sqliteError.Code()&0xff == 13 {
		return fmt.Errorf("%w: %v", ErrCapacity, err)
	}
	return err
}

func fromMilliseconds(value int64) time.Time {
	return time.UnixMilli(value).UTC()
}

func tokenBytes(value sessiontoken.Digest) []byte {
	return value[:]
}
