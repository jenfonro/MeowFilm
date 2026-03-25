package emby

import (
	"encoding/json"
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleHideFromResume(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string, itemID string) {
	current, _, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireProtocolUserMatch(w, current, protocolUserID) {
		return
	}

	if itemID == "" {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}

	req := embyHideFromResumeRequest{}
	_ = json.NewDecoder(r.Body).Decode(&req)
	resp, err := embysvc.BuildHideFromResumeResponse(database, current.Row.ID, itemID, req.Hide)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	writeJSON(w, http.StatusOK, resp)
}
