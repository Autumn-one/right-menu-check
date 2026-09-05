package store

import (
	"context"
	"path/filepath"
	"testing"
	"time"
)

func TestLiveDurationTransfersToSettledTotalWithoutDoubleCounting(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	now := time.Date(2026, 9, 6, 0, 0, 0, 0, time.UTC)
	token := testDigest("duration test")
	check := func(settled, active int64) {
		t.Helper()
		summary, err := dataStore.Summary(ctx)
		if err != nil {
			t.Fatal(err)
		}
		if summary.TotalDurationMS != settled || summary.ActiveDurationMS != active {
			t.Fatalf("duration = settled %d, active %d; want %d, %d", summary.TotalDurationMS, summary.ActiveDurationMS, settled, active)
		}
		machines, err := dataStore.Machines(ctx, 100, 0)
		if err != nil {
			t.Fatal(err)
		}
		var machineSettled, machineActive int64
		for _, machine := range machines {
			machineSettled += machine.TotalDurationMS
			machineActive += machine.ActiveDurationMS
		}
		if machineSettled != settled || machineActive != active {
			t.Fatalf("machine totals = %d, %d; want %d, %d", machineSettled, machineActive, settled, active)
		}
	}
	check(0, 0)
	for _, item := range []struct{ machine, session string }{{machineA, sessionA}, {machineA, sessionB}, {machineB, sessionC}} {
		if _, err := dataStore.Start(ctx, item.machine, item.session, token, now); err != nil {
			t.Fatal(err)
		}
	}
	check(0, 0)
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, token, now.Add(120*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionB, token, now.Add(60*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineB, sessionC, token, now.Add(30*time.Second)); err != nil {
		t.Fatal(err)
	}
	check(0, 210000)
	// Repeated and older heartbeats do not increase or roll back confirmed duration.
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, token, now.Add(120*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, token, now.Add(100*time.Second)); err != nil {
		t.Fatal(err)
	}
	check(0, 210000)
	for range 2 {
		if err := dataStore.End(ctx, machineA, sessionA, token, now.Add(130*time.Second)); err != nil {
			t.Fatal(err)
		}
		check(130000, 90000)
	}
	for range 2 {
		if _, err := dataStore.SettleStale(ctx, now.Add(time.Minute)); err != nil {
			t.Fatal(err)
		}
		check(220000, 0)
	}
	if _, err := dataStore.Resume(ctx, machineB, sessionC, sessionA, token, token, now.Add(10*time.Minute)); err != ErrSessionConflict {
		// The target belongs to another device, and cannot affect either total.
		t.Fatalf("conflicting resume = %v", err)
	}
	check(220000, 0)
	if _, err := dataStore.PruneClosed(ctx, now.Add(time.Hour), now.Add(time.Hour)); err != nil {
		t.Fatal(err)
	}
	check(220000, 0)
}

func TestResumeMovesLiveDurationOnceAndExcludesOfflineGap(t *testing.T) {
	dataStore := openTestStore(t, filepath.Join(t.TempDir(), "telemetry.db"))
	defer dataStore.Close()
	ctx := context.Background()
	now := time.Date(2026, 9, 6, 0, 0, 0, 0, time.UTC)
	token := testDigest("resume duration")
	if _, err := dataStore.Start(ctx, machineA, sessionA, token, now); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionA, token, now.Add(time.Minute)); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		if _, err := dataStore.Resume(ctx, machineA, sessionA, sessionB, token, token, now.Add(time.Hour)); err != nil {
			t.Fatal(err)
		}
	}
	if err := dataStore.Heartbeat(ctx, machineA, sessionB, token, now.Add(time.Hour+2*time.Minute)); err != nil {
		t.Fatal(err)
	}
	assertSummary(t, dataStore, Summary{
		MachineCount: 1, StartupCount: 1, SessionCount: 2, ActiveSessionCount: 1,
		ActiveMachineCount: 1, AbnormalSessionCount: 1, TotalDurationMS: 60000, ActiveDurationMS: 120000,
	})
}
