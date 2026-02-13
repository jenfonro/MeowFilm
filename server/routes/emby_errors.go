package routes

import (
	"net/http"
	"strings"
)

// embyWriteError writes an error response in an Emby-compatible shape.
// Keep this minimal and consistent; clients mainly need a non-200 status + JSON body.
func embyWriteError(w http.ResponseWriter, status int, message string) {
	if w == nil {
		return
	}
	msg := strings.TrimSpace(message)
	if msg == "" {
		msg = http.StatusText(status)
	}
	writeJSON(w, status, map[string]any{
		"Message":   msg,
		"ErrorCode": status,
	})
}
