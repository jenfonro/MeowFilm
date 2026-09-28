package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleItemSimilar(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}
	limit := queryIntDefault(r, "Limit", 15)
	if limit <= 0 {
		limit = 15
	}
	if limit > 15 {
		limit = 15
	}
	payload, built, err := emby_service.BuildSimilarPayload(database, current.Row.ID, serverID, itemID, limit)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if !built {
		writeJSON(w, http.StatusOK, emby_service.EmptySimilarResponse())
		return
	}
	writeJSON(w, http.StatusOK, payload)
}
