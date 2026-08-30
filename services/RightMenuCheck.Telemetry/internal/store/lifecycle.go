package store

import (
	"context"
	"crypto/subtle"
	"database/sql"
	"errors"
	"fmt"
	"time"

	"rightmenucheck.local/telemetry/internal/sessiontoken"
)

type sessionState struct {
	machineID    string
	startupCount int
	tokenHash    []byte
	startedMS    int64
	lastSeenMS   int64
	endedMS      sql.NullInt64
	exitKind     sql.NullString
}

func (s *Store) Start(
	ctx context.Context,
	machineID string,
	sessionID string,
	tokenHash sessiontoken.Digest,
	receivedAt time.Time,
) (StartResult, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return StartResult{}, fmt.Errorf("begin start transaction: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()

	var existingMachine string
	var existingStartedMS int64
	var existingStartupCount int
	var endedMS sql.NullInt64
	err = tx.QueryRowContext(ctx, `
SELECT machine_id, started_at_ms, startup_count, ended_at_ms
FROM sessions WHERE session_id = ?`, sessionID).Scan(
		&existingMachine, &existingStartedMS, &existingStartupCount, &endedMS)
	if err == nil {
		if existingMachine != machineID {
			return StartResult{}, ErrSessionConflict
		}
		if endedMS.Valid {
			return StartResult{}, ErrSessionEnded
		}
		if _, err := tx.ExecContext(ctx,
			"UPDATE sessions SET token_hash = ? WHERE session_id = ?",
			tokenBytes(tokenHash), sessionID); err != nil {
			return StartResult{}, fmt.Errorf("rotate session token: %w", normalizeSQLiteError(err))
		}
		if err := tx.Commit(); err != nil {
			return StartResult{}, fmt.Errorf("commit idempotent start: %w", normalizeSQLiteError(err))
		}
		return StartResult{
			StartupCount: existingStartupCount,
			StartedAt:    fromMilliseconds(existingStartedMS),
		}, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return StartResult{}, fmt.Errorf("find existing session: %w", normalizeSQLiteError(err))
	}

	receivedMS := receivedAt.UTC().UnixMilli()
	machineExists, err := s.machineExists(ctx, tx, machineID)
	if err != nil {
		return StartResult{}, err
	}
	if !machineExists {
		if err := s.ensureMachineCapacity(ctx, tx); err != nil {
			return StartResult{}, err
		}
	}
	if err := s.ensureActiveCapacity(ctx, tx); err != nil {
		return StartResult{}, err
	}
	if err := s.admitNewSession(ctx, tx, receivedMS); err != nil {
		return StartResult{}, err
	}

	if _, err := tx.ExecContext(ctx, `
INSERT INTO machines(machine_id, startup_count, first_started_at_ms, last_started_at_ms)
VALUES (?, 1, ?, ?)
ON CONFLICT(machine_id) DO UPDATE SET
    startup_count = startup_count + 1,
    first_started_at_ms = MIN(first_started_at_ms, excluded.first_started_at_ms),
    last_started_at_ms = MAX(last_started_at_ms, excluded.last_started_at_ms)`,
		machineID, receivedMS, receivedMS); err != nil {
		return StartResult{}, fmt.Errorf("increment startup count: %w", normalizeSQLiteError(err))
	}

	var startupCount int
	if err := tx.QueryRowContext(ctx,
		"SELECT startup_count FROM machines WHERE machine_id = ?",
		machineID).Scan(&startupCount); err != nil {
		return StartResult{}, fmt.Errorf("read startup count: %w", normalizeSQLiteError(err))
	}
	if _, err := tx.ExecContext(ctx, `
INSERT INTO sessions(
    session_id, machine_id, startup_count, token_hash, started_at_ms, last_seen_at_ms)
VALUES (?, ?, ?, ?, ?, ?)`,
		sessionID, machineID, startupCount, tokenBytes(tokenHash), receivedMS, receivedMS); err != nil {
		return StartResult{}, fmt.Errorf("create session: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return StartResult{}, fmt.Errorf("commit start: %w", normalizeSQLiteError(err))
	}
	return StartResult{StartupCount: startupCount, StartedAt: fromMilliseconds(receivedMS)}, nil
}

func (s *Store) Resume(
	ctx context.Context,
	machineID string,
	previousSessionID string,
	sessionID string,
	previousTokenHash sessiontoken.Digest,
	newTokenHash sessiontoken.Digest,
	receivedAt time.Time,
) (StartResult, error) {
	if previousSessionID == sessionID {
		return StartResult{}, ErrSessionConflict
	}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return StartResult{}, fmt.Errorf("begin resume transaction: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()

	previous, err := loadAuthorizedSession(ctx, tx, machineID, previousSessionID, previousTokenHash)
	if err != nil {
		return StartResult{}, err
	}
	if previous.endedMS.Valid && previous.exitKind.String == "normal" {
		return StartResult{}, ErrSessionEnded
	}

	var targetMachine string
	var targetStartupCount int
	var targetStartedMS int64
	var targetEndedMS sql.NullInt64
	err = tx.QueryRowContext(ctx, `
SELECT machine_id, startup_count, started_at_ms, ended_at_ms
FROM sessions WHERE session_id = ?`, sessionID).Scan(
		&targetMachine, &targetStartupCount, &targetStartedMS, &targetEndedMS)
	if err == nil {
		if targetMachine != machineID || targetStartupCount != previous.startupCount {
			return StartResult{}, ErrSessionConflict
		}
		if targetEndedMS.Valid {
			return StartResult{}, ErrSessionEnded
		}
		if _, err := tx.ExecContext(ctx,
			"UPDATE sessions SET token_hash = ? WHERE session_id = ?",
			tokenBytes(newTokenHash), sessionID); err != nil {
			return StartResult{}, fmt.Errorf("rotate resumed session token: %w", normalizeSQLiteError(err))
		}
		if err := tx.Commit(); err != nil {
			return StartResult{}, fmt.Errorf("commit idempotent resume: %w", normalizeSQLiteError(err))
		}
		return StartResult{
			StartupCount: targetStartupCount,
			StartedAt:    fromMilliseconds(targetStartedMS),
		}, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return StartResult{}, fmt.Errorf("find resumed session: %w", normalizeSQLiteError(err))
	}

	if !previous.endedMS.Valid {
		if err := settleSessionAbnormal(ctx, tx, previousSessionID, previous); err != nil {
			return StartResult{}, err
		}
	}
	if err := s.ensureActiveCapacity(ctx, tx); err != nil {
		return StartResult{}, err
	}
	receivedMS := receivedAt.UTC().UnixMilli()
	if err := s.admitNewSession(ctx, tx, receivedMS); err != nil {
		return StartResult{}, err
	}
	if _, err := tx.ExecContext(ctx, `
INSERT INTO sessions(
    session_id, machine_id, startup_count, token_hash, started_at_ms, last_seen_at_ms)
VALUES (?, ?, ?, ?, ?, ?)`,
		sessionID, machineID, previous.startupCount, tokenBytes(newTokenHash), receivedMS, receivedMS); err != nil {
		return StartResult{}, fmt.Errorf("create resumed session: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return StartResult{}, fmt.Errorf("commit resume: %w", normalizeSQLiteError(err))
	}
	return StartResult{
		StartupCount: previous.startupCount,
		StartedAt:    fromMilliseconds(receivedMS),
	}, nil
}

func (s *Store) Heartbeat(
	ctx context.Context,
	machineID string,
	sessionID string,
	tokenHash sessiontoken.Digest,
	receivedAt time.Time,
) error {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin heartbeat transaction: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()

	state, err := loadAuthorizedSession(ctx, tx, machineID, sessionID, tokenHash)
	if err != nil {
		return err
	}
	if state.endedMS.Valid {
		return ErrSessionEnded
	}
	if _, err := tx.ExecContext(ctx, `
UPDATE sessions SET last_seen_at_ms = MAX(last_seen_at_ms, ?)
WHERE session_id = ?`, receivedAt.UTC().UnixMilli(), sessionID); err != nil {
		return fmt.Errorf("update heartbeat: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit heartbeat: %w", normalizeSQLiteError(err))
	}
	return nil
}

func (s *Store) End(
	ctx context.Context,
	machineID string,
	sessionID string,
	tokenHash sessiontoken.Digest,
	receivedAt time.Time,
) error {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin end transaction: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()

	state, err := loadAuthorizedSession(ctx, tx, machineID, sessionID, tokenHash)
	if err != nil {
		return err
	}
	if state.endedMS.Valid {
		if state.exitKind.String == "normal" {
			if err := tx.Commit(); err != nil {
				return fmt.Errorf("commit idempotent end: %w", normalizeSQLiteError(err))
			}
			return nil
		}
		return ErrSessionEnded
	}

	endedMS := max(state.startedMS, state.lastSeenMS, receivedAt.UTC().UnixMilli())
	durationMS := endedMS - state.startedMS
	if _, err := tx.ExecContext(ctx, `
UPDATE sessions
SET ended_at_ms = ?, duration_ms = ?, exit_kind = 'normal'
WHERE session_id = ?`, endedMS, durationMS, sessionID); err != nil {
		return fmt.Errorf("end session: %w", normalizeSQLiteError(err))
	}
	if _, err := tx.ExecContext(ctx, `
UPDATE machines
SET total_duration_ms = total_duration_ms + ?,
    normal_session_count = normal_session_count + 1
WHERE machine_id = ?`, durationMS, machineID); err != nil {
		return fmt.Errorf("aggregate normal session: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit end: %w", normalizeSQLiteError(err))
	}
	return nil
}

func (s *Store) SettleStale(ctx context.Context, cutoff time.Time) (int64, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return 0, fmt.Errorf("begin stale settlement: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()
	cutoffMS := cutoff.UTC().UnixMilli()

	type aggregate struct {
		machineID string
		duration  int64
		count     int64
	}
	rows, err := tx.QueryContext(ctx, `
SELECT machine_id, SUM(last_seen_at_ms - started_at_ms), COUNT(*)
FROM sessions
WHERE ended_at_ms IS NULL AND last_seen_at_ms <= ?
GROUP BY machine_id`, cutoffMS)
	if err != nil {
		return 0, fmt.Errorf("group stale sessions: %w", normalizeSQLiteError(err))
	}
	var aggregates []aggregate
	for rows.Next() {
		var item aggregate
		if err := rows.Scan(&item.machineID, &item.duration, &item.count); err != nil {
			rows.Close()
			return 0, fmt.Errorf("scan stale aggregate: %w", normalizeSQLiteError(err))
		}
		aggregates = append(aggregates, item)
	}
	if err := rows.Close(); err != nil {
		return 0, fmt.Errorf("close stale aggregate query: %w", normalizeSQLiteError(err))
	}
	for _, item := range aggregates {
		if _, err := tx.ExecContext(ctx, `
UPDATE machines
SET total_duration_ms = total_duration_ms + ?,
    abnormal_session_count = abnormal_session_count + ?
WHERE machine_id = ?`, item.duration, item.count, item.machineID); err != nil {
			return 0, fmt.Errorf("aggregate stale sessions: %w", normalizeSQLiteError(err))
		}
	}
	result, err := tx.ExecContext(ctx, `
UPDATE sessions
SET ended_at_ms = last_seen_at_ms,
    duration_ms = last_seen_at_ms - started_at_ms,
    exit_kind = 'abnormal'
WHERE ended_at_ms IS NULL AND last_seen_at_ms <= ?`, cutoffMS)
	if err != nil {
		return 0, fmt.Errorf("settle stale sessions: %w", normalizeSQLiteError(err))
	}
	count, err := result.RowsAffected()
	if err != nil {
		return 0, fmt.Errorf("read settled session count: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return 0, fmt.Errorf("commit stale settlement: %w", normalizeSQLiteError(err))
	}
	return count, nil
}

func (s *Store) PruneClosed(ctx context.Context, cutoff time.Time, now time.Time) (int64, error) {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return 0, fmt.Errorf("begin retention cleanup: %w", normalizeSQLiteError(err))
	}
	defer tx.Rollback()

	result, err := tx.ExecContext(ctx,
		"DELETE FROM sessions WHERE ended_at_ms IS NOT NULL AND ended_at_ms <= ?",
		cutoff.UTC().UnixMilli())
	if err != nil {
		return 0, fmt.Errorf("prune closed sessions: %w", normalizeSQLiteError(err))
	}
	count, err := result.RowsAffected()
	if err != nil {
		return 0, fmt.Errorf("read pruned session count: %w", normalizeSQLiteError(err))
	}
	windowStart := now.UTC().UnixMilli() / 60_000 * 60_000
	if _, err := tx.ExecContext(ctx,
		"DELETE FROM admission_windows WHERE window_started_at_ms < ?",
		windowStart-120_000); err != nil {
		return 0, fmt.Errorf("prune admission windows: %w", normalizeSQLiteError(err))
	}
	if err := tx.Commit(); err != nil {
		return 0, fmt.Errorf("commit retention cleanup: %w", normalizeSQLiteError(err))
	}
	if count > 0 {
		if _, err := s.db.ExecContext(ctx, "PRAGMA wal_checkpoint(TRUNCATE)"); err != nil {
			return count, fmt.Errorf("checkpoint retained session deletion: %w", normalizeSQLiteError(err))
		}
	}
	return count, nil
}

func loadAuthorizedSession(
	ctx context.Context,
	tx *sql.Tx,
	machineID string,
	sessionID string,
	tokenHash sessiontoken.Digest,
) (sessionState, error) {
	var state sessionState
	err := tx.QueryRowContext(ctx, `
SELECT machine_id, startup_count, token_hash, started_at_ms, last_seen_at_ms, ended_at_ms, exit_kind
FROM sessions WHERE session_id = ?`, sessionID).Scan(
		&state.machineID,
		&state.startupCount,
		&state.tokenHash,
		&state.startedMS,
		&state.lastSeenMS,
		&state.endedMS,
		&state.exitKind)
	if errors.Is(err, sql.ErrNoRows) {
		var empty [32]byte
		_ = subtle.ConstantTimeCompare(empty[:], tokenBytes(tokenHash))
		return sessionState{}, ErrSessionNotFound
	}
	if err != nil {
		return sessionState{}, fmt.Errorf("read session: %w", normalizeSQLiteError(err))
	}
	validToken := len(state.tokenHash) == 32 &&
		subtle.ConstantTimeCompare(state.tokenHash, tokenBytes(tokenHash)) == 1
	if !validToken || state.machineID != machineID {
		return sessionState{}, ErrInvalidSessionToken
	}
	return state, nil
}

func settleSessionAbnormal(
	ctx context.Context,
	tx *sql.Tx,
	sessionID string,
	state sessionState,
) error {
	durationMS := state.lastSeenMS - state.startedMS
	if _, err := tx.ExecContext(ctx, `
UPDATE sessions
SET ended_at_ms = last_seen_at_ms,
    duration_ms = last_seen_at_ms - started_at_ms,
    exit_kind = 'abnormal'
WHERE session_id = ? AND ended_at_ms IS NULL`, sessionID); err != nil {
		return fmt.Errorf("settle resumed session: %w", normalizeSQLiteError(err))
	}
	if _, err := tx.ExecContext(ctx, `
UPDATE machines
SET total_duration_ms = total_duration_ms + ?,
    abnormal_session_count = abnormal_session_count + 1
WHERE machine_id = ?`, durationMS, state.machineID); err != nil {
		return fmt.Errorf("aggregate resumed session: %w", normalizeSQLiteError(err))
	}
	return nil
}

func (s *Store) machineExists(ctx context.Context, tx *sql.Tx, machineID string) (bool, error) {
	var exists int
	if err := tx.QueryRowContext(ctx,
		"SELECT EXISTS(SELECT 1 FROM machines WHERE machine_id = ?)", machineID).Scan(&exists); err != nil {
		return false, fmt.Errorf("check machine: %w", normalizeSQLiteError(err))
	}
	return exists == 1, nil
}

func (s *Store) ensureMachineCapacity(ctx context.Context, tx *sql.Tx) error {
	var count int64
	if err := tx.QueryRowContext(ctx, "SELECT COUNT(*) FROM machines").Scan(&count); err != nil {
		return fmt.Errorf("count machines: %w", normalizeSQLiteError(err))
	}
	if count >= s.limits.MaxMachines {
		return ErrCapacity
	}
	return nil
}

func (s *Store) ensureActiveCapacity(ctx context.Context, tx *sql.Tx) error {
	var count int64
	if err := tx.QueryRowContext(ctx,
		"SELECT COUNT(*) FROM sessions WHERE ended_at_ms IS NULL").Scan(&count); err != nil {
		return fmt.Errorf("count active sessions: %w", normalizeSQLiteError(err))
	}
	if count >= s.limits.MaxActiveSessions {
		return ErrCapacity
	}
	return nil
}

func (s *Store) admitNewSession(ctx context.Context, tx *sql.Tx, receivedMS int64) error {
	windowStart := receivedMS / 60_000 * 60_000
	if _, err := tx.ExecContext(ctx,
		"DELETE FROM admission_windows WHERE window_started_at_ms < ?",
		windowStart-120_000); err != nil {
		return fmt.Errorf("prune admission windows: %w", normalizeSQLiteError(err))
	}
	if _, err := tx.ExecContext(ctx, `
INSERT INTO admission_windows(window_started_at_ms, session_count)
VALUES (?, 0) ON CONFLICT(window_started_at_ms) DO NOTHING`, windowStart); err != nil {
		return fmt.Errorf("create admission window: %w", normalizeSQLiteError(err))
	}
	result, err := tx.ExecContext(ctx, `
UPDATE admission_windows SET session_count = session_count + 1
WHERE window_started_at_ms = ? AND session_count < ?`,
		windowStart, s.limits.NewSessionsPerMinute)
	if err != nil {
		return fmt.Errorf("record session admission: %w", normalizeSQLiteError(err))
	}
	updated, err := result.RowsAffected()
	if err != nil {
		return fmt.Errorf("read session admission: %w", normalizeSQLiteError(err))
	}
	if updated == 0 {
		return ErrNewSessionRateLimit
	}
	return nil
}
