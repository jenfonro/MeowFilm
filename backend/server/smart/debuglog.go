package smart

import (
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

var smartDebugLogOnce sync.Once
var smartDebugFileLogger *log.Logger

func smartDebugLogEnabled() bool {
	return strings.TrimSpace(os.Getenv("MEOWFILM_DEBUG")) == "1"
}

func smartDebugLogPath() string {
	return filepath.Clean("debug.log")
}

func smartDebugInitFileLogger() {
	smartDebugLogOnce.Do(func() {
		if !smartDebugLogEnabled() {
			return
		}
		path := smartDebugLogPath()
		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
		if err != nil {
			return
		}
		smartDebugFileLogger = log.New(f, "", log.LstdFlags)
	})
}

func smartDebugPrintf(format string, args ...any) {
	if !smartDebugLogEnabled() {
		return
	}
	log.Printf(format, args...)
	smartDebugInitFileLogger()
	if smartDebugFileLogger != nil {
		smartDebugFileLogger.Printf(format, args...)
	}
}

func smartDebugWriteRaw(prefix string, b []byte) {
	if !smartDebugLogEnabled() || len(b) == 0 {
		return
	}
	smartDebugInitFileLogger()
	if smartDebugFileLogger == nil {
		return
	}
	_ = smartDebugFileLogger.Output(2, prefix)
	_, _ = io.WriteString(smartDebugFileLogger.Writer(), string(b))
	_, _ = io.WriteString(smartDebugFileLogger.Writer(), "\n")
}
