package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyVideos(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) < 2 {
		embyNotFound(w)
		return
	}

	itemID := strings.TrimSpace(parts[0])
	action := strings.ToLower(strings.TrimSpace(parts[1]))
	if itemID == "" {
		embyNotFound(w)
		return
	}

	if action == "stream" || strings.HasPrefix(action, "stream.") {
		handleEmbyVideoStream(w, r, database, serverID, itemID)
		return
	}
	embyNotFound(w)
}

func handleEmbyVideoStream(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, itemID string) {
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		embyMethodNotAllowed(w)
		return
	}

	mediaSourceID := embyQueryTrimCI(r, "MediaSourceId")

	if mediaSourceID != "" {
		if u0, ok := embyStreams.Get(mediaSourceID); ok && strings.TrimSpace(u0) != "" {
			http.Redirect(w, r, strings.TrimSpace(u0), http.StatusFound)
			return
		}
	}

	parsed, ok := embyParseItemID(itemID)
	if !ok || parsed == nil {
		embyNotFound(w)
		return
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		embyWriteError(w, 400, "该条目不可播放")
		return
	}

	playURL, headers, _, err := embyResolvePlaybackFromTMDB(database, u, parsed)
	if err != nil {
		embyBadGateway(w, err)
		return
	}
	finalURL := strings.TrimSpace(playURL)
	finalHeaders := headers

	if len(finalHeaders) == 0 {
		http.Redirect(w, r, finalURL, http.StatusFound)
		return
	}
	embyWriteError(w, 501, "该源需要自定义请求头，暂不支持")
}
