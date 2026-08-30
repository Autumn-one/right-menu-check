package logging

import (
	"fmt"
	"io"
	"log"
	"sync"
	"time"
)

type Logger struct {
	logger *log.Logger
	queue  chan string
	done   chan struct{}
	mutex  sync.RWMutex
	closed bool
}

func New(writer io.Writer, prefix string, flags int, capacity int) *Logger {
	if capacity < 1 {
		capacity = 1
	}
	logger := &Logger{
		logger: log.New(writer, prefix, flags),
		queue:  make(chan string, capacity),
		done:   make(chan struct{}),
	}
	go logger.writeLoop()
	return logger
}

func (l *Logger) Print(values ...any) {
	l.enqueue(fmt.Sprint(values...))
}

func (l *Logger) Printf(format string, values ...any) {
	l.enqueue(fmt.Sprintf(format, values...))
}

func (l *Logger) Close() {
	_ = l.CloseWithin(5 * time.Second)
}

func (l *Logger) CloseWithin(timeout time.Duration) bool {
	l.mutex.Lock()
	if !l.closed {
		l.closed = true
		close(l.queue)
	}
	l.mutex.Unlock()

	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-l.done:
		return true
	case <-timer.C:
		return false
	}
}

func (l *Logger) enqueue(message string) {
	l.mutex.RLock()
	defer l.mutex.RUnlock()
	if l.closed {
		return
	}
	select {
	case l.queue <- message:
	default:
	}
}

func (l *Logger) writeLoop() {
	defer close(l.done)
	for message := range l.queue {
		l.logger.Print(message)
	}
}
