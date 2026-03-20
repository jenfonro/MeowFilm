package api

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

func handleAPIDoubanRexxarProxy(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	// Expect: /api/douban/rexxar/api/v2/...
	path := strings.TrimPrefix(r.URL.Path, "/api")
	if !strings.HasPrefix(path, "/douban/rexxar/api/v2/") {
		http.NotFound(w, r)
		return
	}
	upstreamPath := strings.TrimPrefix(path, "/douban")
	if !strings.HasPrefix(upstreamPath, "/rexxar/api/v2/") {
		http.NotFound(w, r)
		return
	}

	payload, statusCode, err := douban.FetchRexxarJSONWithStatus(database, upstreamPath, r.URL.Query())
	if err != nil {
		writeJSON(w, statusCode, map[string]any{"success": false, "message": err.Error()})
		return
	}
	douban.WriteRawJSON(w, http.StatusOK, payload)
}
