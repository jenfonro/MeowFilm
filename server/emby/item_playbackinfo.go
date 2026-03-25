package emby

import (
	"net/http"

	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
	"github.com/jenfonro/meowfilm/internal/db"
)

func handleItemPlaybackInfo(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}
	payload, built, err := embysvc.BuildPlaybackInfoPayload(database, current.Row.ID, current.Token, serverID, itemID, r)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if !built {
		writeJSON(w, http.StatusOK, embysvc.EmptyPlaybackInfoResponse())
		return
	}
	writeJSON(w, http.StatusOK, payload)
}
