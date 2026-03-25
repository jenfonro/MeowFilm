package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleItemDetail(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if playing, ok := embysvc.GetPlaying(current.Row.ID, clientmeta.ClientDeviceID(r)); ok {
		if embysvc.IsDerivedFromPlaying(playing.ItemID, itemID) {
			writeJSON(w, http.StatusOK, embysvc.BuildQuickNowPlayingDetailPayload(serverID, itemID, playing))
			return
		}
	}
	payload, ok, err := embysvc.BuildItemDetailPayload(database, current.Row.ID, serverID, itemID)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if ok {
		writeJSON(w, http.StatusOK, payload)
		return
	}
	writeEmbyError(w, http.StatusNotFound, "Not Found")
}
