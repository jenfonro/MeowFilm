package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// Jellyfin clients may report playback state via /Sessions/Playing*, but MeowFilm
// doesn't track sessions yet. Accept and no-op to keep clients compatible.
func handleJellyfinSessions(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}

	if len(parts) == 0 {
		http.NotFound(w, r)
		return
	}

	head := strings.ToLower(strings.TrimSpace(parts[0]))
	tail := ""
	if len(parts) >= 2 {
		tail = strings.ToLower(strings.TrimSpace(parts[1]))
	}

	if head == "playing" && (tail == "" || tail == "progress" || tail == "stopped") {
		if r.Method != http.MethodPost {
			methodNotAllowed(w)
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}

	http.NotFound(w, r)
}
