package emby

import (
	"log"
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleVideoStream(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	handlePlaybackRelay(w, r, database, itemID)
}

func handleVideoOriginal(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	handlePlaybackRelay(w, r, database, itemID)
}

func handlePlaybackRelay(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	current, _, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	mediaSourceID := strings.TrimSpace(r.URL.Query().Get("MediaSourceId"))
	playSessionID := strings.TrimSpace(r.URL.Query().Get("PlaySessionId"))
	target, built := embysvc.LoadPlaybackStreamTarget(current.Row.ID, itemID, mediaSourceID, playSessionID)
	if built && target != nil {
		log.Printf("[emby][playback_cache_hit] item=%s source=session finalized=%t", strings.TrimSpace(itemID), strings.TrimSpace(target.FinalURL) != "")
	}
	if built && target != nil && strings.TrimSpace(target.FinalURL) == "" {
		log.Printf("[emby][playback_finalize] item=%s offers=%d", strings.TrimSpace(itemID), len(target.Offers))
		target, built, _ = embysvc.FinalizePlaybackStreamTarget(database, current.Row.ID, *target, "", r)
	}
	if !built || target == nil || strings.TrimSpace(target.FinalURL) == "" {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}
	http.Redirect(w, r, strings.TrimSpace(target.FinalURL), http.StatusFound)
}

func handleVideoStreamM3U8(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	current, _, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	mediaSourceID := strings.TrimSpace(r.URL.Query().Get("MediaSourceId"))
	target, built := embysvc.LoadPlaybackStreamTarget(current.Row.ID, itemID, mediaSourceID, strings.TrimSpace(r.URL.Query().Get("PlaySessionId")))
	if built && target != nil && strings.TrimSpace(target.FinalURL) == "" {
		target, built, _ = embysvc.FinalizePlaybackStreamTarget(database, current.Row.ID, *target, "", r)
	}
	if !built || target == nil || strings.TrimSpace(target.FinalURL) == "" {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}
	w.Header().Set("Content-Type", "application/x-mpegURL")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3600\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:3600.0,\n" + strings.TrimSpace(target.FinalURL) + "\n#EXT-X-ENDLIST\n"))
}
