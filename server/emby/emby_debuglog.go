package emby

import (
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

var embyDebugLogOnce sync.Once
var embyDebugFileLogger *log.Logger

func embyDebugLogEnabled() bool {
	return strings.TrimSpace(os.Getenv("MEOWFILM_DEBUG")) == "1"
}

func embyDebugLogPath() string {
	// Keep it local and predictable; allow relative paths.
	return filepath.Clean("debug.log")
}

func embyDebugInitFileLogger() {
	embyDebugLogOnce.Do(func() {
		if !embyDebugLogEnabled() {
			return
		}
		path := embyDebugLogPath()
		// Truncate on each process start (first init).
		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
		if err != nil {
			// Fall back silently; stdout debug still works.
			return
		}
		embyDebugFileLogger = log.New(f, "", log.LstdFlags)
	})
}

func embyDebugPrintf(format string, args ...any) {
	if !embyDebugLogEnabled() {
		return
	}
	// Always keep stdout behavior when debug is enabled.
	log.Printf(format, args...)

	// Additionally write to debug.log.
	embyDebugInitFileLogger()
	if embyDebugFileLogger != nil {
		embyDebugFileLogger.Printf(format, args...)
	}
}

func embyDebugWriteRaw(prefix string, b []byte) {
	if !embyDebugLogEnabled() || len(b) == 0 {
		return
	}
	embyDebugInitFileLogger()
	if embyDebugFileLogger == nil {
		return
	}
	// Write raw bytes after a prefix line.
	_ = embyDebugFileLogger.Output(2, prefix)
	_, _ = io.WriteString(embyDebugFileLogger.Writer(), string(b))
	_, _ = io.WriteString(embyDebugFileLogger.Writer(), "\n")
}
