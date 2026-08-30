package ratelimit

import (
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestBucketLimitsBurstAndRefills(t *testing.T) {
	now := time.Date(2026, 8, 31, 10, 0, 0, 0, time.UTC)
	bucket := New(2, 3, now)
	for index := 0; index < 3; index++ {
		if !bucket.Allow(now) {
			t.Fatalf("burst request %d was rejected", index)
		}
	}
	if bucket.Allow(now) {
		t.Fatal("request beyond burst was accepted")
	}
	if bucket.Allow(now.Add(499 * time.Millisecond)) {
		t.Fatal("bucket refilled too early")
	}
	if !bucket.Allow(now.Add(500 * time.Millisecond)) {
		t.Fatal("bucket did not refill one token")
	}
}

func TestBucketIsConcurrencySafe(t *testing.T) {
	now := time.Now()
	bucket := New(1, 25, now)
	var accepted atomic.Int64
	var waitGroup sync.WaitGroup
	for index := 0; index < 100; index++ {
		waitGroup.Add(1)
		go func() {
			defer waitGroup.Done()
			if bucket.Allow(now) {
				accepted.Add(1)
			}
		}()
	}
	waitGroup.Wait()
	if accepted.Load() != 25 {
		t.Fatalf("accepted = %d", accepted.Load())
	}
}

func TestBucketRecoversFromClockRollback(t *testing.T) {
	now := time.Now()
	bucket := New(1, 1, now)
	if !bucket.Allow(now) || bucket.Allow(now.Add(-time.Hour)) {
		t.Fatal("clock rollback incorrectly refilled tokens")
	}
	if !bucket.Allow(now.Add(-time.Hour + time.Second)) {
		t.Fatal("bucket did not recover after rollback baseline")
	}
}
