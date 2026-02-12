package routes

import (
	"net/http"
	"net/url"
	"path"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// /jellyfin/media/{itemId}.{ext}
// Some third-party clients treat MediaSources[].Path as a server-relative HTTP path.
// Serve via the standard /jellyfin/Videos stream handler (without additional redirects),
// so clients don't drop auth headers when following a 302 within the same origin.
func handleJellyfinMediaFiles(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if len(parts) < 1 {
		http.NotFound(w, r)
		return
	}
	raw := strings.Trim(strings.TrimSpace(parts[0]), "/")
	if raw == "" {
		http.NotFound(w, r)
		return
	}

	ext := strings.TrimPrefix(strings.ToLower(path.Ext(raw)), ".")
	itemPart := strings.TrimSuffix(raw, path.Ext(raw))
	itemID, _ := url.PathUnescape(itemPart)
	itemID = strings.TrimSpace(itemID)
	if itemID == "" {
		http.NotFound(w, r)
		return
	}

	container := ext
	if container == "" {
		container = "mp4"
	}
	mediaSourceID := jellyfinStableHex32(itemID)

	r2 := r.Clone(r.Context())
	q := r2.URL.Query()
	q.Set("static", "true")
	q.Set("MediaSourceId", mediaSourceID)
	r2.URL.RawQuery = q.Encode()
	handleJellyfinVideoStream(w, r2, database, serverID, itemID)
}
