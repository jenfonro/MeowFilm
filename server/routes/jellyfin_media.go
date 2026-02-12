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
// Redirect to the standard /jellyfin/Videos stream endpoint.
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

	base := jellyfinBaseURL(r)
	target := strings.TrimRight(base, "/") + "/jellyfin/Videos/" + url.PathEscape(itemID) + "/stream." + url.PathEscape(container)
	u2, _ := url.Parse(target)
	q := u2.Query()
	q.Set("static", "true")
	q.Set("MediaSourceId", mediaSourceID)
	if tok := jellyfinReadToken(r); tok != "" {
		q.Set("api_key", tok)
	}
	u2.RawQuery = q.Encode()
	http.Redirect(w, r, u2.String(), http.StatusFound)
}
