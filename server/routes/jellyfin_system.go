package routes

import (
	"net/http"
	"runtime"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinSystem(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) >= 2 && strings.EqualFold(parts[0], "Info") && strings.EqualFold(parts[1], "Public") && r.Method == http.MethodGet {
		siteName := defaultString(database.GetSetting("site_name"), "MeowFilm")
		writeJSON(w, 200, map[string]any{
			"ServerName":      siteName,
			"Version":         "10.9.0",
			"ProductName":     "Jellyfin",
			"Id":              serverID,
			"OperatingSystem": runtime.GOOS,
		})
		return
	}
	http.NotFound(w, r)
}
