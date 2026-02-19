package emby

import (
	"net/http"
	"runtime"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbySystem(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) >= 2 && strings.EqualFold(parts[0], "Info") && strings.EqualFold(parts[1], "Public") && r.Method == http.MethodGet {
		cfg, _ := database.ReadAppConfig()
		siteName := defaultString(cfg.SiteName, "MeowFilm")
		writeJSON(w, 200, map[string]any{
			"ServerName":      siteName,
			"Version":         "10.9.0",
			"ProductName":     "Emby",
			"Id":              serverID,
			"OperatingSystem": runtime.GOOS,
		})
		return
	}
	embyNotFound(w)
}
