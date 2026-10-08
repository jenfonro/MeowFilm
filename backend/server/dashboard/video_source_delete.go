package dashboard

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleDashboardVideoSourceSiteDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("key"))
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
		return
	}
	deleted, err := database.DeleteVideoSourceSite(key)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "删除站点失败"})
		return
	}
	if !deleted {
		writeJSON(w, http.StatusNotFound, map[string]any{"success": false, "message": "站点不存在或已删除"})
		return
	}
	sites := mergeVideoSourceSites(database)
	cfg, _ := database.ReadAppConfig()
	cover := resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
	writeJSON(w, http.StatusOK, map[string]any{
		"success":   true,
		"key":       key,
		"sites":     sites,
		"coverSite": cover,
	})
}
