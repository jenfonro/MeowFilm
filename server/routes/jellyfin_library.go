package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinLibrary(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Library/VirtualFolders
	// Many clients probe this endpoint during initial connection.
	if len(parts) >= 1 && strings.EqualFold(parts[0], "VirtualFolders") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		// Return empty list; we don't maintain real library folders.
		writeJSON(w, 200, []any{})
		return
	}
	http.NotFound(w, r)
}
