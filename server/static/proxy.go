package static

import (
	"net/http"
	"strconv"
	"strings"
)

// WriteProxiedResponse writes a small, in-memory proxied response body to the client,
// forwarding only safe headers that are relevant for static-like assets.
func WriteProxiedResponse(w http.ResponseWriter, resp *http.Response, body []byte) {
	if w == nil {
		return
	}
	if body == nil {
		body = []byte{}
	}

	status := http.StatusOK
	if resp != nil {
		status = resp.StatusCode
		if ct := strings.TrimSpace(resp.Header.Get("Content-Type")); ct != "" {
			w.Header().Set("Content-Type", ct)
		}
		if cc := strings.TrimSpace(resp.Header.Get("Cache-Control")); cc != "" {
			w.Header().Set("Cache-Control", cc)
		}
	}

	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(status)
	_, _ = w.Write(body)
}
