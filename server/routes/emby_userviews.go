package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyUserViews(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /UserViews/GroupingOptions?userId=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "GroupingOptions") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		userID := embyQueryTrimCI(r, "UserId")
		if !embyAllowEmptyOrRequireSameUserOrNotFound(w, u.ID, userID) {
			return
		}
		writeJSON(w, 200, embyDefaultGroupingOptions())
		return
	}

	// GET /UserViews?userId=...
	if len(parts) == 0 && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		userID := embyQueryTrimCI(r, "UserId")
		if !embyAllowEmptyOrRequireSameUserOrNotFound(w, u.ID, userID) {
			return
		}
		writeJSON(w, 200, embyDefaultUserViewsResponse(serverID))
		return
	}

	embyNotFound(w)
}
