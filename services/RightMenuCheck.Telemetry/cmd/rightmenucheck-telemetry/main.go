package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"rightmenucheck.local/telemetry/internal/api"
	"rightmenucheck.local/telemetry/internal/buildinfo"
	"rightmenucheck.local/telemetry/internal/cleanup"
	"rightmenucheck.local/telemetry/internal/config"
	"rightmenucheck.local/telemetry/internal/logging"
	"rightmenucheck.local/telemetry/internal/store"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--version" {
		fmt.Println(buildinfo.Version)
		return
	}

	logger := logging.New(os.Stdout, "rightmenucheck-telemetry: ", log.Ldate|log.Ltime|log.LUTC, 64)
	if err := run(logger); err != nil {
		logger.Print("service stopped due to an unrecoverable error")
		_ = logger.CloseWithin(2 * time.Second)
		os.Exit(1)
	}
	_ = logger.CloseWithin(2 * time.Second)
}

func run(logger cleanup.Logger) error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}

	rootCtx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	dataStore, err := store.OpenWithLimits(rootCtx, cfg.DatabasePath, store.Limits{
		NewSessionsPerMinute: cfg.NewSessionsPerMinute,
		MaxMachines:          cfg.MaxMachines,
		MaxActiveSessions:    cfg.MaxActiveSessions,
		MaxDatabaseBytes:     cfg.MaxDatabaseBytes,
	})
	if err != nil {
		return err
	}
	defer dataStore.Close()

	cleanupWorker := cleanup.New(
		dataStore,
		cfg.SessionTimeout,
		cfg.ClosedSessionTTL,
		cfg.SweepInterval,
		logger,
	)
	settleCtx, cancelSettle := context.WithTimeout(rootCtx, 10*time.Second)
	cleanupResult, err := cleanupWorker.RunNow(settleCtx)
	cancelSettle()
	if err != nil {
		return err
	}
	if cleanupResult.Settled > 0 {
		logger.Printf("settled %d stale telemetry session(s) during startup", cleanupResult.Settled)
	}
	if cleanupResult.Pruned > 0 {
		logger.Printf("pruned %d retained telemetry session(s) during startup", cleanupResult.Pruned)
	}
	cleanupWorker.Start(rootCtx)
	defer func() {
		stop()
		cleanupWorker.Wait()
	}()

	listener, err := net.Listen("tcp", cfg.ListenAddress)
	if err != nil {
		return err
	}
	defer listener.Close()
	tcpAddress, ok := listener.Addr().(*net.TCPAddr)
	if !ok || !tcpAddress.IP.IsLoopback() {
		return errors.New("listener resolved outside the loopback interface")
	}

	handler := api.New(dataStore, api.Options{
		Clock:                             time.Now,
		AdminToken:                        cfg.AdminToken,
		AllowUnauthenticatedLoopbackAdmin: cfg.AllowUnauthenticatedLoopbackAdmin,
		MaxRequestBytes:                   cfg.MaxRequestBytes,
		MaxConcurrent:                     cfg.MaxConcurrent,
		HandlerTimeout:                    cfg.HandlerTimeout,
		RequestsPerSecond:                 cfg.RequestsPerSecond,
		RequestBurst:                      cfg.RequestBurst,
	}).Handler()
	httpServer := &http.Server{
		Handler:           handler,
		ReadHeaderTimeout: min(5*time.Second, cfg.HandlerTimeout),
		ReadTimeout:       cfg.HandlerTimeout,
		WriteTimeout:      cfg.HandlerTimeout + 2*time.Second,
		IdleTimeout:       60 * time.Second,
		MaxHeaderBytes:    16 << 10,
		ErrorLog:          stdLoggerDiscard(),
	}

	serveErrors := make(chan error, 1)
	go func() {
		serveErrors <- httpServer.Serve(listener)
	}()
	logger.Printf("listening on %s", listener.Addr())

	select {
	case <-rootCtx.Done():
		shutdownCtx, cancelShutdown := context.WithTimeout(context.Background(), cfg.ShutdownTimeout)
		defer cancelShutdown()
		if err := httpServer.Shutdown(shutdownCtx); err != nil {
			_ = httpServer.Close()
			return err
		}
	case err := <-serveErrors:
		if !errors.Is(err, http.ErrServerClosed) {
			return err
		}
	}

	logger.Print("shutdown complete")
	return nil
}

func stdLoggerDiscard() *log.Logger {
	return log.New(io.Discard, "", 0)
}
