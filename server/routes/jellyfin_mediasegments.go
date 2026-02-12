package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// /MediaSegments/{itemId}
// Jellyfin uses this for intro/credits markers. MeowFilm doesn't generate these yet,
// so return an empty list (valid response) instead of 404 to keep clients happy.
func handleJellyfinMediaSegments(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if len(parts) < 1 || strings.TrimSpace(parts[0]) == "" {
		http.NotFound(w, r)
		return
	}
	writeJSON(w, 200, []any{})
}
