package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tvmeta"
)

func handleAPITVMeta(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	id, _ := strconv.Atoi(strings.TrimSpace(r.URL.Query().Get("id")))
	if id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	data, err := tvmeta.GetTVMeta(database, id)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TV Meta 请求失败",
			"code":  "TV_META_REQUEST_FAILED",
			"raw":   err.Error(),
		})
		return
	}
	writeJSON(w, http.StatusOK, data)
}
