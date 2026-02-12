package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinDisplayPreferences(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /DisplayPreferences/usersettings?userId=...&client=...
	// Clients often probe this during connection. Return a minimal object.
	if len(parts) >= 1 && strings.EqualFold(parts[0], "usersettings") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		writeJSON(w, 200, map[string]any{
			"Id":                   "usersettings",
			"Client":               defaultString(r.URL.Query().Get("client"), "Infuse"),
			"UserId":               defaultString(r.URL.Query().Get("userId"), ""),
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

	http.NotFound(w, r)
}
