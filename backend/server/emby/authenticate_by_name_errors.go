package emby

import (
	"net/http"
	"strings"
)

type embyErrorResponse struct {
	Message   string `json:"Message"`
	ErrorCode int    `json:"ErrorCode"`
}

func writeEmbyError(w http.ResponseWriter, status int, message string) {
	if w == nil {
		return
	}
	msg := strings.TrimSpace(message)
	if msg == "" {
		msg = http.StatusText(status)
	}
	writeJSON(w, status, embyErrorResponse{
		Message:   msg,
		ErrorCode: status,
	})
}
