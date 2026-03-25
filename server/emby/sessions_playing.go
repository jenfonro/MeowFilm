package emby

import (
	"encoding/json"
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleSessionsPlaying(w http.ResponseWriter, r *http.Request, database *db.DB, tail string) {
	current, _, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodPost {
		writeEmbyError(w, http.StatusMethodNotAllowed, "Method Not Allowed")
		return
	}
	var payload embysvc.SessionPlaybackPayload
	_ = json.NewDecoder(r.Body).Decode(&payload)
	deviceID := strings.TrimSpace(clientmeta.ClientDeviceID(r))
	payload.ItemID = strings.TrimSpace(payload.ItemID)
	payload.MediaSourceID = strings.TrimSpace(payload.MediaSourceID)
	payload.PlaySessionID = strings.TrimSpace(payload.PlaySessionID)
	cacheKey := embysvc.ResolvePlaybackCacheKey(strconv.FormatInt(current.Row.ID, 10), deviceID, payload.ItemID)
	guardEntry := embysvc.PlayingGuardEntry{ItemID: payload.ItemID}
	if ref := embysvc.ParseItemRefAnyPublic(payload.ItemID); ref != nil {
		if ref.SubKind == "episode" {
			guardEntry.SeasonNumber = ref.Pan
			guardEntry.EpisodeNo = ref.Episode
		}
	}
	switch strings.ToLower(strings.TrimSpace(tail)) {
	case "stopped":
		_ = embysvc.HandleSessionStopped(database, current.Row.ID, payload)
		embysvc.ClearPlaying(current.Row.ID, deviceID)
		embysvc.MarkPlaybackSessionStopped(payload.PlaySessionID, payload.MediaSourceID, cacheKey)
	case "progress":
		embysvc.NotePlayingProgress(current.Row.ID, deviceID, guardEntry)
		embysvc.MarkPlaybackSessionStarted(payload.PlaySessionID, payload.MediaSourceID, cacheKey)
		_ = embysvc.HandleSessionProgress(database, current.Row.ID, payload)
	default:
		embysvc.NotePlayingProgress(current.Row.ID, deviceID, guardEntry)
		embysvc.MarkPlaybackSessionStarted(payload.PlaySessionID, payload.MediaSourceID, cacheKey)
		_ = embysvc.HandleSessionPlaying(database, current.Row.ID, payload)
	}
	w.WriteHeader(http.StatusNoContent)
}
