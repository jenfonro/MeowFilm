package api

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

func handleAPIDoubanSearch(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	keyword := strings.TrimSpace(r.URL.Query().Get("q"))
	if keyword == "" {
		keyword = strings.TrimSpace(r.URL.Query().Get("search_text"))
	}
	if keyword == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "missing q"})
		return
	}

	result, fromCache, err := douban.FetchSearchPayload(database, keyword)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}
	if result == nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": "douban search payload empty"})
		return
	}
	result["cache_status"] = fromCache
	writeJSON(w, http.StatusOK, result)
}
