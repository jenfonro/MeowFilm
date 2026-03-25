package emby

import (
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleItemPrimaryImage(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	writeEmbyCommonHeaders(w.Header())
	userID := optionalCurrentUserID(database, r)
	target, _ := embysvc.ResolvePrimaryImageTarget(database, userID, strings.TrimSpace(itemID), strings.TrimSpace(r.URL.Query().Get("tag")))
	writeResolvedItemImage(w, r, strings.TrimSpace(target))
}

func handleItemBackdropImage(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	writeEmbyCommonHeaders(w.Header())
	target, _ := embysvc.ResolveBackdropImageTarget(database, strings.TrimSpace(itemID), strings.TrimSpace(r.URL.Query().Get("maxWidth")))
	writeResolvedItemImage(w, r, strings.TrimSpace(target))
}

func handleItemLogoImage(w http.ResponseWriter, r *http.Request, database *db.DB, itemID string) {
	writeEmbyCommonHeaders(w.Header())
	target, _ := embysvc.ResolveLogoImageTarget(database, strings.TrimSpace(itemID), strings.TrimSpace(r.URL.Query().Get("maxWidth")))
	writeResolvedItemImage(w, r, strings.TrimSpace(target))
}

func writeResolvedItemImage(w http.ResponseWriter, r *http.Request, target string) {
	if strings.TrimSpace(target) == "" {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}
	if shouldProxyImageForClient(r) {
		proxyResolvedItemImage(w, r, target)
		return
	}
	http.Redirect(w, r, target, http.StatusFound)
}

func shouldProxyImageForClient(r *http.Request) bool {
	meta := clientmeta.ResolveRequestClientMeta(r)
	client := strings.ToLower(strings.TrimSpace(meta.Client))
	if strings.Contains(client, "infuse") {
		return true
	}
	ua := strings.ToLower(strings.TrimSpace(r.UserAgent()))
	return strings.Contains(ua, "infuse")
}

func proxyResolvedItemImage(w http.ResponseWriter, r *http.Request, target string) {
	req, err := http.NewRequestWithContext(r.Context(), http.MethodGet, target, nil)
	if err != nil {
		writeEmbyError(w, http.StatusBadGateway, "Bad Gateway")
		return
	}
	req.Header.Set("Accept", "*/*")

	client := &http.Client{
		Timeout: 20 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return http.ErrUseLastResponse
			}
			return nil
		},
	}
	resp, err := client.Do(req)
	if err != nil {
		writeEmbyError(w, http.StatusBadGateway, "Bad Gateway")
		return
	}
	defer func() { _ = resp.Body.Close() }()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return
	}

	for _, key := range []string{"Content-Type", "Content-Length", "Cache-Control", "ETag", "Expires", "Last-Modified"} {
		if v := strings.TrimSpace(resp.Header.Get(key)); v != "" {
			w.Header().Set(key, v)
		}
	}
	w.WriteHeader(http.StatusOK)
	_, _ = io.Copy(w, resp.Body)
}
