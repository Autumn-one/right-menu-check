package logging

import (
	"bytes"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestLoggerWritesOnDedicatedGoroutineAndDrainsOnClose(t *testing.T) {
	var output synchronizedBuffer
	logger := New(&output, "service: ", 0, 8)
	logger.Print("started")
	logger.Printf("settled %d session(s)", 2)
	logger.Close()
	logger.Close()

	actual := output.String()
	if !strings.Contains(actual, "service: started") || !strings.Contains(actual, "service: settled 2 session(s)") {
		t.Fatalf("unexpected output: %q", actual)
	}
	logger.Print("ignored after close")
	if strings.Contains(output.String(), "ignored") {
		t.Fatal("logger accepted a message after Close")
	}
}

func TestCloseWithinDoesNotHangOnBlockedWriter(t *testing.T) {
	writer := &blockingWriter{started: make(chan struct{}), release: make(chan struct{})}
	logger := New(writer, "", 0, 1)
	logger.Print("blocked")
	<-writer.started
	if logger.CloseWithin(10 * time.Millisecond) {
		t.Fatal("CloseWithin() reported success while the writer was blocked")
	}
	close(writer.release)
	if !logger.CloseWithin(time.Second) {
		t.Fatal("CloseWithin() did not finish after the writer was released")
	}
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

type blockingWriter struct {
	started chan struct{}
	release chan struct{}
	once    sync.Once
}

func (w *blockingWriter) Write(value []byte) (int, error) {
	w.once.Do(func() { close(w.started) })
	<-w.release
	return len(value), nil
}
