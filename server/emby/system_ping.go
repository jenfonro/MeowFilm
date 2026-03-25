package emby

import "net/http"

func handleSystemPing(w http.ResponseWriter, r *http.Request, database any) {
	w.Header().Set("Content-Type", "text/plain")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte("Emby Server"))
}
