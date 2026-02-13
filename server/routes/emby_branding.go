package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyBranding(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Branding/Configuration
	if len(parts) >= 1 && strings.EqualFold(parts[0], "Configuration") && r.Method == http.MethodGet {
		writeJSON(w, 200, map[string]any{
			"LoginDisclaimer":     "",
			"CustomCss":           "",
			"SplashscreenEnabled": false,
		})
		return
	}
	embyNotFound(w)
}
