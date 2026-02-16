package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyDisplayPreferences(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /DisplayPreferences/usersettings?userId=...&client=...
	// Clients often probe this during connection. Return a minimal object.
	if len(parts) >= 1 && strings.EqualFold(parts[0], "usersettings") && r.Method == http.MethodGet {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		client := embyQueryTrimCI(r, "client")
		if client == "" {
			client = "Infuse"
		}
		userID := embyQueryTrimCI(r, "userId")
		writeJSON(w, 200, map[string]any{
			"Id":                   "usersettings",
			"Client":               client,
			"UserId":               userID,
			"ScrollDirection":      "Horizontal",
			"CustomPrefs":          map[string]any{},
			"SortBy":               "SortName",
			"SortOrder":            "Ascending",
			"RememberIndexing":     true,
			"RememberSorting":      true,
			"RememberGrouping":     true,
			"RememberViewStyle":    true,
			"RememberLanguage":     true,
			"RememberSubtitleMode": true,
		})
		return
	}

	embyNotFound(w)
}
