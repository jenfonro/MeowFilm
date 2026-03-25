package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleUserItemsResume(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireProtocolUserMatch(w, current, protocolUserID) {
		return
	}

	startIndex := queryIntDefault(r, "StartIndex", 0)
	if startIndex < 0 {
		startIndex = 0
	}
	limit := queryIntDefault(r, "Limit", 12)
	if limit <= 0 {
		limit = 12
	}
	if limit > 60 {
		limit = 60
	}

	resp, err := embysvc.BuildResumePayload(database, current.Row.ID, serverID, limit, startIndex)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	writeJSON(w, http.StatusOK, resp)
}
