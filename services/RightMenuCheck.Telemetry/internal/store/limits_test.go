package store

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"testing"
	"time"
)

func TestMachineAndActiveCapacityRejectOnlyNewSessions(t *testing.T) {
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 8, 0, 0, 0, time.UTC)
	limits := DefaultLimits()
	limits.MaxMachines = 1
	limits.MaxActiveSessions = 1
	dataStore := openLimitedTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"), limits)
	defer dataStore.Close()
	tokenA := testDigest("a")

	if _, err := dataStore.Start(ctx, machineA, sessionA, tokenA, startedAt); err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Start(ctx, machineA, sessionB, testDigest("b"), startedAt); !errors.Is(err, ErrCapacity) {
		t.Fatalf("active capacity error = %v", err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, tokenA, startedAt.Add(time.Second)); err != nil {
		t.Fatalf("capacity blocked existing heartbeat: %v", err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, tokenA, startedAt.Add(2*time.Second)); err != nil {
		t.Fatalf("capacity blocked existing end: %v", err)
	}
	if _, err := dataStore.Start(ctx, machineB, sessionB, testDigest("b"), startedAt); !errors.Is(err, ErrCapacity) {
		t.Fatalf("machine capacity error = %v", err)
	}
	if _, err := dataStore.Start(ctx, machineA, sessionB, testDigest("b"), startedAt); err != nil {
		t.Fatalf("existing machine could not create a session after capacity freed: %v", err)
	}
}

func TestNewSessionRateLimitPersistsAcrossRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	limits := DefaultLimits()
	limits.NewSessionsPerMinute = 1
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 9, 0, 0, 0, time.UTC)
	dataStore := openLimitedTestStore(t, path, limits)
	tokenA := testDigest("a")
	if _, err := dataStore.Start(ctx, machineA, sessionA, tokenA, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(ctx, machineA, sessionA, tokenA, startedAt.Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Close(); err != nil {
		t.Fatal(err)
	}

	dataStore = openLimitedTestStore(t, path, limits)
	defer dataStore.Close()
	if _, err := dataStore.Start(
		ctx, machineA, sessionB, testDigest("b"), startedAt.Add(30*time.Second)); !errors.Is(err, ErrNewSessionRateLimit) {
		t.Fatalf("persisted rate limit error = %v", err)
	}
	if _, err := dataStore.Start(
		ctx, machineA, sessionB, testDigest("b"), startedAt.Add(time.Minute)); err != nil {
		t.Fatalf("new minute was not admitted: %v", err)
	}
}

func TestDatabasePageLimitFailsClosed(t *testing.T) {
	limits := DefaultLimits()
	limits.MaxDatabaseBytes = 128 << 10
	limits.MaxMachines = 10_000
	limits.MaxActiveSessions = 10_000
	limits.NewSessionsPerMinute = 10_000
	dataStore := openLimitedTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"), limits)
	defer dataStore.Close()
	ctx := context.Background()
	startedAt := time.Date(2026, 8, 31, 10, 0, 0, 0, time.UTC)

	capacityReached := false
	for index := 1; index <= 2000; index++ {
		machineID := fmt.Sprintf("%064x", index)
		sessionID := fmt.Sprintf("%032x", index)
		token := testDigest(sessionID)
		_, err := dataStore.Start(ctx, machineID, sessionID, token, startedAt)
		if errors.Is(err, ErrCapacity) {
			capacityReached = true
			break
		}
		if err != nil {
			t.Fatalf("unexpected start error at %d: %v", index, err)
		}
		if err := dataStore.End(ctx, machineID, sessionID, token, startedAt); err != nil {
			t.Fatalf("end at %d: %v", index, err)
		}
	}
	if !capacityReached {
		t.Fatal("SQLite max_page_count did not enforce the configured capacity")
	}
}

func TestCanceledContextStopsStoreOperation(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err := dataStore.Start(ctx, machineA, sessionA, testDigest("token"), time.Now())
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled operation error = %v", err)
	}
}
