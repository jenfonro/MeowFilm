package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyMediaSegments(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if r.Method != http.MethodGet {
		embyMethodNotAllowed(w)
		return
	}
	_, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if len(parts) < 1 || strings.TrimSpace(parts[0]) == "" {
		embyNotFound(w)
		return
	}
	embyWriteEmptyArrayOK(w)
}
