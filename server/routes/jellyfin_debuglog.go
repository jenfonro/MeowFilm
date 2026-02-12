package routes

import (
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

var jellyfinDebugLogOnce sync.Once
var jellyfinDebugFileLogger *log.Logger

func jellyfinDebugLogEnabled() bool {
	return strings.TrimSpace(os.Getenv("MEOWFILM_JELLYFIN_DEBUG_LOG")) == "1"
}

func jellyfinDebugLogPath() string {
	// Keep it local and predictable; allow relative paths.
	return filepath.Clean("debug.log")
}

func jellyfinDebugInitFileLogger() {
	jellyfinDebugLogOnce.Do(func() {
		if !jellyfinDebugLogEnabled() {
			return
		}
		path := jellyfinDebugLogPath()
		// Truncate on each process start (first init).
		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
		if err != nil {
			// Fall back silently; stdout debug still works.
			return
		}
		jellyfinDebugFileLogger = log.New(f, "", log.LstdFlags)
	})
}

func jellyfinDebugPrintf(format string, args ...any) {
	if !jellyfinDebugLogEnabled() {
		return
	}
	// Always keep stdout behavior when debug is enabled.
	log.Printf(format, args...)

	// Additionally write to debug.log.
	jellyfinDebugInitFileLogger()
	if jellyfinDebugFileLogger != nil {
		jellyfinDebugFileLogger.Printf(format, args...)
	}
}

func jellyfinDebugWriteRaw(prefix string, b []byte) {
	if !jellyfinDebugLogEnabled() || len(b) == 0 {
		return
	}
	jellyfinDebugInitFileLogger()
	if jellyfinDebugFileLogger == nil {
		return
	}
	// Write raw bytes after a prefix line.
	_ = jellyfinDebugFileLogger.Output(2, prefix)
	_, _ = io.WriteString(jellyfinDebugFileLogger.Writer(), string(b))
	_, _ = io.WriteString(jellyfinDebugFileLogger.Writer(), "\n")
}
