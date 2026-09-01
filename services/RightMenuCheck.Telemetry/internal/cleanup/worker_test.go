package cleanup

import (
	"bytes"
	"context"
	"crypto/sha256"
	"log"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"rightmenucheck.local/telemetry/internal/sessiontoken"
	"rightmenucheck.local/telemetry/internal/store"
)

func TestWorkerSettlesStaleSessionsOnDedicatedGoroutine(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "telemetry.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()

	const machineID = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	const sessionID = "00112233445566778899aabbccddeeff"
	startedAt := time.Date(2026, 8, 31, 8, 0, 0, 0, time.UTC)
	token := digest("worker")
	if _, err := dataStore.Start(context.Background(), machineID, sessionID, token, startedAt); err != nil {
		t.Fatal(err)
	}

	var logs synchronizedBuffer
	worker := New(
		dataStore,
		time.Minute,
		time.Hour,
		5*time.Millisecond,
		log.New(&logs, "", 0),
	)
	worker.clock = func() time.Time { return startedAt.Add(2 * time.Minute) }
	ctx, cancel := context.WithCancel(context.Background())
	worker.Start(ctx)

	deadline := time.Now().Add(time.Second)
	for {
		if strings.Contains(logs.String(), "settled 1 stale telemetry session") {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("cleanup goroutine did not settle the session")
		}
		time.Sleep(time.Millisecond)
	}
	cancel()
	worker.Wait()

	summary, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if summary.AbnormalSessionCount != 1 {
		t.Fatalf("cleanup did not aggregate the stale session: %#v", summary)
	}
	if strings.Contains(logs.String(), machineID) || strings.Contains(logs.String(), sessionID) {
		t.Fatalf("cleanup log contains an identifier: %q", logs.String())
	}
}

func TestRunNowPrunesRetainedRowsWithoutChangingAggregates(t *testing.T) {
	dataStore, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "telemetry.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()

	const machineID = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	const sessionID = "00112233445566778899aabbccddeeff"
	startedAt := time.Date(2026, 8, 31, 9, 0, 0, 0, time.UTC)
	token := digest("retention")
	if _, err := dataStore.Start(context.Background(), machineID, sessionID, token, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.End(
		context.Background(), machineID, sessionID, token, startedAt.Add(30*time.Second)); err != nil {
		t.Fatal(err)
	}
	before, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}

	worker := New(
		dataStore,
		time.Minute,
		time.Hour,
		time.Second,
		log.New(&bytes.Buffer{}, "", 0),
	)
	worker.clock = func() time.Time { return startedAt.Add(2 * time.Hour) }
	result, err := worker.RunNow(context.Background())
	if err != nil || result.Pruned != 1 || result.Settled != 0 {
		t.Fatalf("maintenance = %#v, %v", result, err)
	}
	after, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if before != after {
		t.Fatalf("aggregates changed after retention: before=%#v after=%#v", before, after)
	}
	rows, err := dataStore.Sessions(
		context.Background(), "", 10, 0, startedAt.Add(3*time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 0 {
		t.Fatalf("closed session was retained: %#v", rows)
	}
}

func TestRunNowRecoversStaleSessionAfterDatabaseRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "telemetry.db")
	const machineID = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	const sessionID = "00112233445566778899aabbccddeeff"
	startedAt := time.Date(2026, 8, 31, 10, 0, 0, 0, time.UTC)
	token := digest("restart")

	dataStore, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := dataStore.Start(context.Background(), machineID, sessionID, token, startedAt); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Heartbeat(
		context.Background(), machineID, sessionID, token, startedAt.Add(30*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := dataStore.Close(); err != nil {
		t.Fatal(err)
	}

	dataStore, err = store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer dataStore.Close()
	worker := New(
		dataStore,
		time.Minute,
		24*time.Hour,
		time.Second,
		log.New(&bytes.Buffer{}, "", 0),
	)
	worker.clock = func() time.Time { return startedAt.Add(2 * time.Minute) }
	result, err := worker.RunNow(context.Background())
	if err != nil || result.Settled != 1 {
		t.Fatalf("restart maintenance = %#v, %v", result, err)
	}
	summary, err := dataStore.Summary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if summary.AbnormalSessionCount != 1 || summary.TotalDurationMS != 30_000 {
		t.Fatalf("unexpected recovered aggregate: %#v", summary)
	}
}

func digest(value string) sessiontoken.Digest {
	return sha256.Sum256([]byte(value))
}

type synchronizedBuffer struct {
	mutex sync.Mutex
	bytes.Buffer
}

func (b *synchronizedBuffer) Write(value []byte) (int, error) {
	b.mutex.Lock()
	defer b.mutex.Unlock()
	return b.Buffer.Write(value)
}

func (b *synchronizedBuffer) String() string {
	b.mutex.Lock()
	defer b.mutex.Unlock()
	return b.Buffer.String()
}
