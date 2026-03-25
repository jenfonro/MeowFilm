package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleShowSeasons(w http.ResponseWriter, r *http.Request, database *db.DB, seriesID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}
	resp, ok, err := buildShowSeasonsByShape(r, database, current.Row.ID, serverID, strings.TrimSpace(seriesID))
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

func buildShowSeasonsByShape(r *http.Request, database *db.DB, userID int64, serverID string, seriesID string) (any, bool, error) {
	switch ResolveSeasonQueryShape(r) {
	case SeasonQueryShapeBasic:
		return embysvc.BuildShowSeasonsBasicPayload(database, userID, serverID, seriesID)
	case SeasonQueryShapeGenres:
		return embysvc.BuildShowSeasonsPayload(database, userID, serverID, seriesID)
	case SeasonQueryShapeRichDetail:
		if WantsFamilySeasonPayload(r) {
			return embysvc.BuildShowSeasonsFamilyPayload(database, userID, serverID, seriesID)
		}
		return embysvc.BuildShowSeasonsLennaPayload(database, userID, serverID, seriesID)
	default:
		return embysvc.BuildShowSeasonsPayload(database, userID, serverID, seriesID)
	}
}
