package routes

import (
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinVideos(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
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
	_, ok := jellyfinRequireUser(w, r, database)
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
			handleJellyfinStream(w, r, database, serverID, []string{mediaSourceID})
			return
		}
	}

	u, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
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

	streamID := jellyfinNewStreamID()
	jellyfinStreams.Lock()
	jellyfinStreams.M[streamID] = jellyfinStreamSession{
		URL:     finalURL,
		Headers: finalHeaders,
		Expire:  time.Now().Add(20 * time.Minute),
	}
	jellyfinStreams.Unlock()

	base := jellyfinBaseURL(r)
	mediaPath := strings.TrimRight(base, "/") + "/jellyfin/stream/" + url.PathEscape(streamID)
	if tok := jellyfinReadToken(r); tok != "" {
		u2, _ := url.Parse(mediaPath)
		q2 := u2.Query()
		q2.Set("api_key", tok)
		u2.RawQuery = q2.Encode()
		mediaPath = u2.String()
	}
	http.Redirect(w, r, mediaPath, http.StatusFound)
}
