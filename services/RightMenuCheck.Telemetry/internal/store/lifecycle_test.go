package store

import (
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"testing"
	"time"

	"rightmenucheck.local/telemetry/internal/sessiontoken"
)

const (
	machineA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	machineB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	sessionA = "00112233445566778899aabbccddeeff"
	sessionB = "10112233445566778899aabbccddeeff"
	sessionC = "20112233445566778899aabbccddeeff"
)

func TestLifecycleAuthenticatesAndAggregatesNormalExit(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	dataStore := openTestStore(t, path)
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 2, 3, 4, 567_000_000, time.UTC)
	token := testDigest("valid token")
	forged := testDigest("forged token")

	started, err := dataStore.Start(ctx, machineA, sessionA, token, startedAt)
	if err != nil {
		t.Fatal(err)
	}
	if started.StartupCount != 1 || !started.StartedAt.Equal(startedAt) {
		t.Fatalf("unexpected start result: %#v", started)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, forged, startedAt.Add(time.Second)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("forged heartbeat error = %v", err)
	}
	if err := dataStore.Heartbeat(ctx, machineB, sessionA, token, startedAt.Add(time.Second)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("wrong-machine heartbeat error = %v", err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, token, startedAt.Add(12*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, token, startedAt.Add(30*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, token, startedAt.Add(31*time.Second)); err != nil {
		t.Fatalf("idempotent end failed: %v", err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, forged, startedAt.Add(31*time.Second)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("forged idempotent end error = %v", err)
	}

	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 1, SessionCount: 1,
		NormalSessionCount: 1, TotalDurationMS: 30_000,
	})
	machines, err := dataStore.Machines(ctx, 10, 0)
	if err != nil {
		t.Fatal(err)
	}
	if len(machines) != 1 || machines[0].TotalDurationMS != 30_000 ||
		machines[0].NormalSessionCount != 1 || machines[0].AbnormalSessionCount != 0 {
		t.Fatalf("unexpected machine aggregate: %#v", machines)
	}

	var stored []byte
	if err := dataStore.db.QueryRow(
		"SELECT token_hash FROM sessions WHERE session_id = ?", sessionA).Scan(&stored); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(stored, token[:]) {
		t.Fatal("database did not store the expected token digest")
	}
}

func TestStartRetryRotatesTokenWithoutIncrementing(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 3, 0, 0, 0, time.UTC)
	firstToken := testDigest("first")
	secondToken := testDigest("second")

	first, err := dataStore.Start(ctx, machineA, sessionA, firstToken, startedAt)
	if err != nil {
		t.Fatal(err)
	}
	retry, err := dataStore.Start(ctx, machineA, sessionA, secondToken, startedAt.Add(time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if first.StartupCount != 1 || retry.StartupCount != 1 || !retry.StartedAt.Equal(startedAt) {
		t.Fatalf("retry changed startup identity: first=%#v retry=%#v", first, retry)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, firstToken, startedAt.Add(time.Hour)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("old token remained valid: %v", err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, secondToken, startedAt.Add(time.Hour)); err != nil {
		t.Fatalf("rotated token failed: %v", err)
	}
	if _, err := dataStore.Start(ctx, machineB, sessionA, secondToken, startedAt); !errors.Is(err, ErrSessionConflict) {
		t.Fatalf("session collision error = %v", err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, secondToken, startedAt.Add(2*time.Hour)); err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Start(ctx, machineA, sessionA, testDigest("third"), startedAt); !errors.Is(err, ErrSessionEnded) {
		t.Fatalf("closed-session restart error = %v", err)
	}
}

func TestResumeCreatesAuthenticatedSegmentWithoutStartupIncrement(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 4, 0, 0, 0, time.UTC)
	firstToken := testDigest("first segment")
	secondToken := testDigest("second segment")
	thirdToken := testDigest("retry rotation")

	if _, err := dataStore.Start(ctx, machineA, sessionA, firstToken, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, firstToken, startedAt.Add(20*time.Second)); err != nil {
		t.Fatal(err)
	}
	if count, err := dataStore.SettleStale(ctx, startedAt.Add(20*time.Second)); err != nil || count != 1 {
		t.Fatalf("stale settlement = %d, %v", count, err)
	}
	if _, err := dataStore.Resume(
		ctx, machineA, sessionA, sessionB, testDigest("forged"), secondToken, startedAt.Add(time.Hour)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("forged resume error = %v", err)
	}

	resumed, err := dataStore.Resume(
		ctx, machineA, sessionA, sessionB, firstToken, secondToken, startedAt.Add(time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if resumed.StartupCount != 1 || !resumed.StartedAt.Equal(startedAt.Add(time.Hour)) {
		t.Fatalf("unexpected resume result: %#v", resumed)
	}
	retried, err := dataStore.Resume(
		ctx, machineA, sessionA, sessionB, firstToken, thirdToken, startedAt.Add(2*time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if retried.StartupCount != 1 || !retried.StartedAt.Equal(startedAt.Add(time.Hour)) {
		t.Fatalf("resume retry changed segment identity: %#v", retried)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionB, secondToken, startedAt.Add(time.Hour)); !errors.Is(err, ErrInvalidSessionToken) {
		t.Fatalf("old resumed token remained valid: %v", err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionB, thirdToken, startedAt.Add(time.Hour+5*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(ctx, machineA, sessionB, thirdToken, startedAt.Add(time.Hour+10*time.Second)); err != nil {
		t.Fatal(err)
	}

	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 1, SessionCount: 2,
		NormalSessionCount: 1, AbnormalSessionCount: 1, TotalDurationMS: 30_000,
	})
}

func TestResumeSettlesStillActiveSegmentAtomically(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 5, 0, 0, 0, time.UTC)
	firstToken := testDigest("active")
	secondToken := testDigest("resumed")

	if _, err := dataStore.Start(ctx, machineA, sessionA, firstToken, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, firstToken, startedAt.Add(15*time.Second)); err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Resume(
		ctx, machineA, sessionA, sessionB, firstToken, secondToken, startedAt.Add(time.Hour)); err != nil {
		t.Fatal(err)
	}
	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 1, SessionCount: 2, ActiveSessionCount: 1,
		ActiveMachineCount: 1, AbnormalSessionCount: 1, TotalDurationMS: 15_000,
	})
}

func TestResumeAfterServiceRestartKeepsOriginalStartupCount(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 5, 30, 0, 0, time.UTC)
	firstToken := testDigest("before restart")
	secondToken := testDigest("after restart")

	dataStore := openTestStore(t, path)
	if _, err := dataStore.Start(ctx, machineA, sessionA, firstToken, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(
		ctx, machineA, sessionA, firstToken, startedAt.Add(30*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Close(); err != nil {
		t.Fatal(err)
	}

	dataStore = openTestStore(t, path)
	defer dataStore.Close()
	if count, err := dataStore.SettleStale(ctx, startedAt.Add(2*time.Minute)); err != nil || count != 1 {
		t.Fatalf("restart settlement = %d, %v", count, err)
	}
	resumed, err := dataStore.Resume(
		ctx,
		machineA,
		sessionA,
		sessionB,
		firstToken,
		secondToken,
		startedAt.Add(3*time.Minute),
	)
	if err != nil {
		t.Fatal(err)
	}
	if resumed.StartupCount != 1 {
		t.Fatalf("resume after restart startup count = %d", resumed.StartupCount)
	}
	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 1, SessionCount: 2, ActiveSessionCount: 1,
		ActiveMachineCount: 1, AbnormalSessionCount: 1, TotalDurationMS: 30_000,
	})
}

func TestRetentionDeletesRawSessionsWithoutLosingAggregates(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 6, 0, 0, 0, time.UTC)
	token := testDigest("retained")

	if _, err := dataStore.Start(ctx, machineA, sessionA, token, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, token, startedAt.Add(10*time.Second)); err != nil {
		t.Fatal(err)
	}
	before, err := dataStore.Summary(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if count, err := dataStore.PruneClosed(ctx, startedAt.Add(9*time.Second), startedAt.Add(time.Hour)); err != nil || count != 0 {
		t.Fatalf("early prune = %d, %v", count, err)
	}
	if count, err := dataStore.PruneClosed(ctx, startedAt.Add(10*time.Second), startedAt.Add(time.Hour)); err != nil || count != 1 {
		t.Fatalf("cutoff prune = %d, %v", count, err)
	}
	after, err := dataStore.Summary(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if before != after {
		t.Fatalf("summary changed across retention: before=%#v after=%#v", before, after)
	}
	rows, err := dataStore.Sessions(ctx, "", 10, 0, startedAt.Add(time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 0 {
		t.Fatalf("retained rows remain: %#v", rows)
	}
}

func TestSessionsCanBeFilteredByMachineWithLifecycleTimes(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 6, 30, 0, 0, time.UTC)
	firstToken := testDigest("first machine")
	secondToken := testDigest("second machine")
	if _, err := dataStore.Start(ctx, machineA, sessionA, firstToken, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(
		ctx, machineA, sessionA, firstToken, startedAt.Add(20*time.Second)); err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Start(
		ctx, machineB, sessionB, secondToken, startedAt.Add(time.Minute)); err != nil {
		t.Fatal(err)
	}

	rows, err := dataStore.Sessions(ctx, machineA, 10, 0, startedAt.Add(time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || rows[0].MachineID != machineA || rows[0].EndedAt == nil ||
		!rows[0].StartedAt.Equal(startedAt) ||
		!rows[0].LastSeenAt.Equal(startedAt) ||
		!rows[0].EndedAt.Equal(startedAt.Add(20*time.Second)) ||
		rows[0].DurationMS != 20_000 || rows[0].ExitKind != "normal" {
		t.Fatalf("filtered sessions = %#v", rows)
	}
}

func TestConcurrentStartsAreCountedExactlyOnce(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 7, 0, 0, 0, time.UTC)
	const count = 32

	results := make(chan int, count)
	errorsChannel := make(chan error, count)
	var waitGroup sync.WaitGroup
	for index := 0; index < count; index++ {
		waitGroup.Add(1)
		go func(index int) {
			defer waitGroup.Done()
			sessionID := fmt.Sprintf("%032x", index+1)
			result, err := dataStore.Start(
				ctx, machineA, sessionID, testDigest(sessionID), startedAt)
			if err != nil {
				errorsChannel <- err
				return
			}
			results <- result.StartupCount
		}(index)
	}
	waitGroup.Wait()
	close(results)
	close(errorsChannel)
	for err := range errorsChannel {
		t.Errorf("Start() failed: %v", err)
	}
	counts := make([]int, 0, count)
	for result := range results {
		counts = append(counts, result)
	}
	sort.Ints(counts)
	if len(counts) != count {
		t.Fatalf("received %d results", len(counts))
	}
	for index, actual := range counts {
		if actual != index+1 {
			t.Fatalf("sorted count[%d] = %d", index, actual)
		}
	}
}

func TestOnlyTokenDigestIsPersisted(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	dataStore := openTestStore(t, path)
	raw := bytes.Repeat([]byte{0x7b}, 32)
	token, digest, err := sessiontoken.GenerateFrom(bytes.NewReader(raw))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Start(context.Background(), machineA, sessionA, digest, time.Now()); err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.db.Exec("PRAGMA wal_checkpoint(TRUNCATE)"); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Close(); err != nil {
		t.Fatal(err)
	}

	for _, suffix := range []string{"", "-wal", "-shm"} {
		content, err := os.ReadFile(path + suffix)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			t.Fatal(err)
		}
		if bytes.Contains(content, []byte(token)) || bytes.Contains(content, raw) {
			t.Fatalf("raw token persisted in %s", filepath.Base(path+suffix))
		}
	}
}

func testDigest(value string) sessiontoken.Digest {
	return sha256.Sum256([]byte(value))
}

func openTestStore(t *testing.T, path string) *Store {
	t.Helper()
	dataStore, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	return dataStore
}

func openLimitedTestStore(t *testing.T, path string, limits Limits) *Store {
	t.Helper()
	dataStore, err := OpenWithLimits(context.Background(), path, limits)
	if err != nil {
		t.Fatal(err)
	}
	return dataStore
}

func assertSummary(t *testing.T, dataStore *Store, expected Summary) {
	t.Helper()
	actual, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if actual != expected {
		t.Fatalf("summary = %#v, want %#v", actual, expected)
	}
}
