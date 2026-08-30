package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"strings"
	"testing"
	"time"

	_ "modernc.org/sqlite"
)

const legacySchema = `
CREATE TABLE machines (
    machine_id TEXT PRIMARY KEY,
    startup_count INTEGER NOT NULL,
    first_started_at_ms INTEGER NOT NULL,
    last_started_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE sessions (
    session_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL REFERENCES machines(machine_id),
    startup_count INTEGER NOT NULL,
    started_at_ms INTEGER NOT NULL,
    last_seen_at_ms INTEGER NOT NULL,
    ended_at_ms INTEGER,
    duration_ms INTEGER,
    exit_kind TEXT
) STRICT;
PRAGMA user_version = 1;
`

func TestV1MigrationSettlesActiveRowsAndPreservesTotals(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(legacySchema); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(
		"INSERT INTO machines VALUES (?, 3, 1000, 3000)", machineA); err != nil {
		t.Fatal(err)
	}
	for _, statement := range []string{
		"INSERT INTO sessions VALUES ('00112233445566778899aabbccddeeff', '" + machineA + "', 1, 1000, 1100, 1100, 100, 'normal')",
		"INSERT INTO sessions VALUES ('10112233445566778899aabbccddeeff', '" + machineA + "', 2, 2000, 2050, 2050, 50, 'abnormal')",
		"INSERT INTO sessions VALUES ('20112233445566778899aabbccddeeff', '" + machineA + "', 3, 3000, 3075, NULL, NULL, NULL)",
	} {
		if _, err := db.Exec(statement); err != nil {
			t.Fatal(err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}

	dataStore := openTestStore(t, path)
	defer dataStore.Close()
	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 3, SessionCount: 3,
		NormalSessionCount: 1, AbnormalSessionCount: 2, TotalDurationMS: 225,
	})
	var version int
	if err := dataStore.db.QueryRow("PRAGMA user_version").Scan(&version); err != nil {
		t.Fatal(err)
	}
	if version != currentSchemaVersion {
		t.Fatalf("schema version = %d", version)
	}
	var active, missingTokens int
	if err := dataStore.db.QueryRow(`
SELECT COUNT(*) FILTER (WHERE ended_at_ms IS NULL),
       COUNT(*) FILTER (WHERE token_hash IS NULL)
FROM sessions`).Scan(&active, &missingTokens); err != nil {
		t.Fatal(err)
	}
	if active != 0 || missingTokens != 3 {
		t.Fatalf("migration state active=%d missingTokens=%d", active, missingTokens)
	}
	if count, err := dataStore.PruneClosed(
		context.Background(), time.UnixMilli(4000), time.UnixMilli(4000)); err != nil || count != 3 {
		t.Fatalf("migration retention = %d, %v", count, err)
	}
	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 3, SessionCount: 3,
		NormalSessionCount: 1, AbnormalSessionCount: 2, TotalDurationMS: 225,
	})
}

func TestSchemaContainsOnlyBoundedTelemetryFields(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()

	expected := map[string][]string{
		"machines": {
			"machine_id", "startup_count", "first_started_at_ms", "last_started_at_ms",
			"total_duration_ms", "normal_session_count", "abnormal_session_count",
		},
		"sessions": {
			"session_id", "machine_id", "startup_count", "token_hash", "started_at_ms",
			"last_seen_at_ms", "ended_at_ms", "duration_ms", "exit_kind",
		},
		"admission_windows": {"window_started_at_ms", "session_count"},
	}
	for table, expectedColumns := range expected {
		rows, err := dataStore.db.Query("PRAGMA table_info(" + table + ")")
		if err != nil {
			t.Fatal(err)
		}
		var columns []string
		for rows.Next() {
			var cid, notNull, primaryKey int
			var name, columnType string
			var defaultValue any
			if err := rows.Scan(&cid, &name, &columnType, &notNull, &defaultValue, &primaryKey); err != nil {
				rows.Close()
				t.Fatal(err)
			}
			columns = append(columns, name)
		}
		if err := rows.Close(); err != nil {
			t.Fatal(err)
		}
		if strings.Join(columns, ",") != strings.Join(expectedColumns, ",") {
			t.Fatalf("%s columns = %v", table, columns)
		}
	}
}

func TestOpenRejectsFutureSchemaVersion(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec("PRAGMA user_version = 3"); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	if dataStore, err := Open(context.Background(), path); err == nil {
		dataStore.Close()
		t.Fatal("Open() accepted a future schema")
	}
}
