package cleanup

import (
	"context"
	"sync"
	"time"

	"rightmenucheck.local/telemetry/internal/store"
)

type Logger interface {
	Print(...any)
	Printf(string, ...any)
}

type Worker struct {
	store     *store.Store
	timeout   time.Duration
	retention time.Duration
	interval  time.Duration
	clock     func() time.Time
	logger    Logger
	waitGroup sync.WaitGroup
	startOnce sync.Once
}

type Result struct {
	Settled int64
	Pruned  int64
}

func New(
	dataStore *store.Store,
	timeout time.Duration,
	retention time.Duration,
	interval time.Duration,
	logger Logger,
) *Worker {
	return &Worker{
		store:     dataStore,
		timeout:   timeout,
		retention: retention,
		interval:  interval,
		clock:     time.Now,
		logger:    logger,
	}
}

func (w *Worker) RunNow(ctx context.Context) (Result, error) {
	now := w.clock().UTC()
	settled, err := w.store.SettleStale(ctx, now.Add(-w.timeout))
	if err != nil {
		return Result{}, err
	}
	pruned, err := w.store.PruneClosed(ctx, now.Add(-w.retention), now)
	if err != nil {
		return Result{}, err
	}
	return Result{Settled: settled, Pruned: pruned}, nil
}

func (w *Worker) Start(ctx context.Context) {
	w.startOnce.Do(func() {
		w.waitGroup.Add(1)
		go func() {
			defer w.waitGroup.Done()
			ticker := time.NewTicker(w.interval)
			defer ticker.Stop()
			for {
				select {
				case <-ctx.Done():
					return
				case <-ticker.C:
					settleCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
					result, err := w.RunNow(settleCtx)
					cancel()
					if err != nil {
						w.logger.Print("telemetry cleanup failed")
					} else {
						if result.Settled > 0 {
							w.logger.Printf("settled %d stale telemetry session(s)", result.Settled)
						}
						if result.Pruned > 0 {
							w.logger.Printf("pruned %d retained telemetry session(s)", result.Pruned)
						}
					}
				}
			}
		}()
	})
}

func (w *Worker) Wait() {
	w.waitGroup.Wait()
}
