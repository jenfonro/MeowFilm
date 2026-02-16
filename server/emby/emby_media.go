package emby

import (
	"net/http"
	"net/url"
	"path"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// /emby/media/{itemId}.{ext}
// Some third-party clients treat MediaSources[].Path as a server-relative HTTP path.
// Serve via the standard /emby/Videos stream handler (without additional redirects),
// so clients don't drop auth headers when following a 302 within the same origin.
func handleEmbyMediaFiles(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		embyMethodNotAllowed(w)
		return
	}
	if len(parts) < 1 {
		embyNotFound(w)
		return
	}
	raw := strings.Trim(strings.TrimSpace(parts[0]), "/")
	if raw == "" {
		embyNotFound(w)
		return
	}

	ext := strings.TrimPrefix(strings.ToLower(path.Ext(raw)), ".")
	itemPart := strings.TrimSuffix(raw, path.Ext(raw))
	itemID, _ := url.PathUnescape(itemPart)
	itemID = strings.TrimSpace(itemID)
	if itemID == "" {
		embyNotFound(w)
		return
	}

	container := ext
	if container == "" {
		container = "mp4"
	}
	mediaSourceID := embyStableHex32(itemID)

	r2 := r.Clone(r.Context())
	q := r2.URL.Query()
	q.Set("static", "true")
	q.Set("MediaSourceId", mediaSourceID)
	r2.URL.RawQuery = q.Encode()
	handleEmbyVideoStream(w, r2, database, serverID, itemID)
}
