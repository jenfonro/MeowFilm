package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleShowEpisodes(w http.ResponseWriter, r *http.Request, database *db.DB, seriesID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}
	seasonID := strings.TrimSpace(r.URL.Query().Get("SeasonId"))
	resp, ok, err := buildShowEpisodesByShape(r, database, current.Row.ID, serverID, strings.TrimSpace(seriesID), seasonID)
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

func buildShowEpisodesByShape(r *http.Request, database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (any, bool, error) {
	switch ResolveEpisodeQueryShape(r) {
	case EpisodeQueryShapeBasic:
		return embysvc.BuildShowEpisodesBasicPayload(database, userID, serverID, seriesID, seasonID)
	case EpisodeQueryShapeInfuse:
		return embysvc.BuildShowEpisodesInfusePayload(database, userID, serverID, seriesID, seasonID)
	case EpisodeQueryShapeRich:
		if WantsFamilyEpisodePayload(r) {
			return embysvc.BuildShowEpisodesFamilyPayload(database, userID, serverID, seriesID, seasonID)
		}
		return embysvc.BuildShowEpisodesLennaPayload(database, userID, serverID, seriesID, seasonID)
	default:
		return embysvc.BuildShowEpisodesPayload(database, userID, serverID, seriesID, seasonID)
	}
}
