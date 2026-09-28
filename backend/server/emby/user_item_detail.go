package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleUserItemDetail(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string, itemID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireProtocolUserMatch(w, current, protocolUserID) {
		return
	}
	payload, ok, err := embysvc.BuildUserItemDetailPayload(database, current.Row.ID, serverID, strings.TrimSpace(itemID))
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if !ok {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}
	writeJSON(w, http.StatusOK, payload)
}
