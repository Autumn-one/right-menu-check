package ratelimit

import (
	"sync"
	"time"
)

type Bucket struct {
	mutex    sync.Mutex
	rate     float64
	capacity float64
	tokens   float64
	last     time.Time
}

func New(ratePerSecond float64, burst int, now time.Time) *Bucket {
	capacity := float64(burst)
	return &Bucket{
		rate:     ratePerSecond,
		capacity: capacity,
		tokens:   capacity,
		last:     now,
	}
}

func (b *Bucket) Allow(now time.Time) bool {
	b.mutex.Lock()
	defer b.mutex.Unlock()

	elapsed := now.Sub(b.last).Seconds()
	if elapsed < 0 {
		b.last = now
		elapsed = 0
	} else if elapsed > 0 {
		b.last = now
	}
	b.tokens = min(b.capacity, b.tokens+elapsed*b.rate)
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}
