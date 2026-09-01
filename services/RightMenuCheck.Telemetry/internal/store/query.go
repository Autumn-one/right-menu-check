package store

import (
	"context"
	"database/sql"
	"fmt"
	"time"
)

func (s *Store) Summary(ctx context.Context) (Summary, error) {
	var result Summary
	err := s.db.QueryRowContext(ctx, `
SELECT
    COUNT(*),
    COALESCE(SUM(startup_count), 0),
    COALESCE(SUM(normal_session_count + abnormal_session_count), 0) +
        (SELECT COUNT(*) FROM sessions WHERE ended_at_ms IS NULL),
    (SELECT COUNT(*) FROM sessions WHERE ended_at_ms IS NULL),
    (SELECT COUNT(DISTINCT machine_id) FROM sessions WHERE ended_at_ms IS NULL),
    COALESCE(SUM(normal_session_count), 0),
    COALESCE(SUM(abnormal_session_count), 0),
    COALESCE(SUM(total_duration_ms), 0)
FROM machines`).Scan(
		&result.MachineCount,
		&result.StartupCount,
		&result.SessionCount,
		&result.ActiveSessionCount,
		&result.ActiveMachineCount,
		&result.NormalSessionCount,
		&result.AbnormalSessionCount,
		&result.TotalDurationMS)
	if err != nil {
		return Summary{}, fmt.Errorf("read summary: %w", normalizeSQLiteError(err))
	}
	return result, nil
}

func (s *Store) Machines(ctx context.Context, limit, offset int) ([]Machine, error) {
	rows, err := s.db.QueryContext(ctx, `
SELECT m.machine_id, m.startup_count, m.first_started_at_ms, m.last_started_at_ms,
       m.total_duration_ms, m.normal_session_count, m.abnormal_session_count,
       (SELECT COUNT(*) FROM sessions s
        WHERE s.machine_id = m.machine_id AND s.ended_at_ms IS NULL),
       COALESCE((SELECT MAX(s.last_seen_at_ms) FROM sessions s
                 WHERE s.machine_id = m.machine_id), m.last_started_at_ms)
FROM machines m
ORDER BY m.last_started_at_ms DESC, m.machine_id
LIMIT ? OFFSET ?`, limit, offset)
	if err != nil {
		return nil, fmt.Errorf("query machines: %w", normalizeSQLiteError(err))
	}
	defer rows.Close()

	var result []Machine
	for rows.Next() {
		var row Machine
		var firstMS, lastMS, lastSeenMS int64
		if err := rows.Scan(
			&row.MachineID,
			&row.StartupCount,
			&firstMS,
			&lastMS,
			&row.TotalDurationMS,
			&row.NormalSessionCount,
			&row.AbnormalSessionCount,
			&row.ActiveSessionCount,
			&lastSeenMS); err != nil {
			return nil, fmt.Errorf("scan machine: %w", normalizeSQLiteError(err))
		}
		row.FirstStartedAt = fromMilliseconds(firstMS)
		row.LastStartedAt = fromMilliseconds(lastMS)
		row.LastSeenAt = fromMilliseconds(lastSeenMS)
		result = append(result, row)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate machines: %w", normalizeSQLiteError(err))
	}
	return result, nil
}

func (s *Store) Sessions(
	ctx context.Context,
	machineID string,
	limit int,
	offset int,
	now time.Time,
) ([]Session, error) {
	rows, err := s.db.QueryContext(ctx, `
SELECT machine_id,
       started_at_ms,
       last_seen_at_ms,
       ended_at_ms,
       CASE WHEN duration_ms IS NULL THEN MAX(0, MIN(?, last_seen_at_ms) - started_at_ms)
            ELSE duration_ms END,
       COALESCE(exit_kind, 'active')
FROM sessions
WHERE (? = '' OR machine_id = ?)
ORDER BY started_at_ms DESC, session_id
LIMIT ? OFFSET ?`, now.UTC().UnixMilli(), machineID, machineID, limit, offset)
	if err != nil {
		return nil, fmt.Errorf("query sessions: %w", normalizeSQLiteError(err))
	}
	defer rows.Close()

	var result []Session
	for rows.Next() {
		var row Session
		var startedMS, lastSeenMS int64
		var endedMS sql.NullInt64
		if err := rows.Scan(
			&row.MachineID,
			&startedMS,
			&lastSeenMS,
			&endedMS,
			&row.DurationMS,
			&row.ExitKind); err != nil {
			return nil, fmt.Errorf("scan session: %w", normalizeSQLiteError(err))
		}
		row.StartedAt = fromMilliseconds(startedMS)
		row.LastSeenAt = fromMilliseconds(lastSeenMS)
		if endedMS.Valid {
			endedAt := fromMilliseconds(endedMS.Int64)
			row.EndedAt = &endedAt
		}
		result = append(result, row)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate sessions: %w", normalizeSQLiteError(err))
	}
	return result, nil
}
