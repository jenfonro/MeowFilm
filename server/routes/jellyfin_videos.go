package routes

import (
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinVideos(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) < 2 {
		http.NotFound(w, r)
		return
	}

	itemID := strings.TrimSpace(parts[0])
	action := strings.ToLower(strings.TrimSpace(parts[1]))
	if itemID == "" {
		http.NotFound(w, r)
		return
	}

	if action == "stream" || strings.HasPrefix(action, "stream.") {
		handleJellyfinVideoStream(w, r, database, serverID, itemID)
		return
	}
	http.NotFound(w, r)
}

func handleJellyfinVideoStream(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, itemID string) {
	u, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}

	q := r.URL.Query()
	mediaSourceID := strings.TrimSpace(q.Get("MediaSourceId"))
	if mediaSourceID == "" {
		mediaSourceID = strings.TrimSpace(q.Get("mediaSourceId"))
	}

	if mediaSourceID != "" {
		jellyfinStreams.Lock()
		sess, ok := jellyfinStreams.M[mediaSourceID]
		now := time.Now()
		for k, v := range jellyfinStreams.M {
			if v.Expire.Before(now) {
				delete(jellyfinStreams.M, k)
			}
		}
		jellyfinStreams.Unlock()

		if ok && strings.TrimSpace(sess.URL) != "" {
			http.Redirect(w, r, strings.TrimSpace(sess.URL), http.StatusFound)
			return
		}
	}

	parsed, ok := jellyfinParseItemID(itemID)
	if !ok || parsed == nil {
		http.NotFound(w, r)
		return
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		jellyfinWriteError(w, 400, "该条目不可播放")
		return
	}

	playURL, headers, err := jellyfinResolvePlaybackFromTMDB(database, u, parsed)
	if err != nil {
		jellyfinWriteError(w, 502, err.Error())
		return
	}
	finalURL := strings.TrimSpace(playURL)
	finalHeaders := headers

	if len(finalHeaders) == 0 {
		http.Redirect(w, r, finalURL, http.StatusFound)
		return
	}
	jellyfinWriteError(w, 501, "该源需要自定义请求头，暂不支持")
}
