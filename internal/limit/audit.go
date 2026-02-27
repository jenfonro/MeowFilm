package limit

import (
	"fmt"
	"os"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/buildinfo"
)

var (
	auditMu   sync.Mutex
	auditLast = map[string]time.Time{}
)

func Audit(key string, msg string) {
	wm := buildinfo.WatermarkTrim()
	if wm == "" || key == "" || msg == "" {
		return
	}
	now := time.Now()
	auditMu.Lock()
	last := auditLast[key]
	if !last.IsZero() && now.Sub(last) < 5*time.Second {
		auditMu.Unlock()
		return
	}
	auditLast[key] = now
	auditMu.Unlock()

	_, _ = fmt.Fprintf(os.Stderr, "a=%s %s\n", wm, msg)
}
