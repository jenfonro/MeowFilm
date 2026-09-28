package emby

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleShowNextUp(w http.ResponseWriter, r *http.Request, database *db.DB) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}
	limit := 1
	if raw := strings.TrimSpace(r.URL.Query().Get("Limit")); raw != "" {
		if n, err := strconv.Atoi(raw); err == nil && n > 0 {
			limit = n
		}
	}
	resp, ok, err := embysvc.BuildShowNextUpPayload(database, current.Row.ID, serverID, strings.TrimSpace(r.URL.Query().Get("SeriesId")), limit)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if !ok {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}
	writeJSON(w, http.StatusOK, resp)
}
