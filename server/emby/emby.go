package emby

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func EmbyHandler(database *db.DB) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		rec := &responseRecorder{ResponseWriter: w}
		defer func() {
			if embyDebugEnabled() && !embyShouldSkipDebugLog(r) {
				log.Printf("[emby] %s %s -> %d (%d bytes) cost=%s", r.Method, embyURL(r), rec.Status(), rec.bytes, time.Since(start))
			}
		}()

		writeEmbyCommonHeaders(rec.Header())
		if r.Method == http.MethodOptions {
			rec.WriteHeader(http.StatusNoContent)
			return
		}

		tail := strings.Trim(embyRouteTail(r), "/")
		if r.Method == http.MethodPost && strings.EqualFold(tail, "Users/AuthenticateByName") {
			handleAuthenticateByName(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "System/Configuration") {
			handleSystemConfiguration(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "System/Info/Public") {
			handleSystemInfoPublic(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "System/Ping") {
			handleSystemPing(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "DisplayPreferences/usersettings") {
			handleDisplayPreferencesUserSettings(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "Library/VirtualFolders") {
			handleLibraryVirtualFolders(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "Genres") {
			handleGenres(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && strings.EqualFold(tail, "Items/Counts") {
			handleItemsCounts(rec, r, database)
			return
		}
		parts := splitEmbyPath(tail)
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Views") {
			handleUserViews(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodPost && len(parts) == 5 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Items") && strings.EqualFold(parts[4], "HideFromResume") {
			handleHideFromResume(rec, r, database, parts[1], parts[3])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Items") && strings.EqualFold(parts[3], "Resume") {
			handleUserItemsResume(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 2 && strings.EqualFold(parts[0], "Shows") && strings.EqualFold(parts[1], "NextUp") {
			handleShowNextUp(rec, r, database)
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Shows") && strings.EqualFold(parts[2], "Seasons") {
			handleShowSeasons(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Shows") && strings.EqualFold(parts[2], "Episodes") {
			handleShowEpisodes(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "Similar") {
			handleItemSimilar(rec, r, database, parts[1])
			return
		}
		if (r.Method == http.MethodPost || r.Method == http.MethodGet) && len(parts) == 3 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "PlaybackInfo") {
			handleItemPlaybackInfo(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodPost && len(parts) >= 2 && strings.EqualFold(parts[0], "Sessions") && strings.EqualFold(parts[1], "Playing") {
			tailPart := ""
			if len(parts) >= 3 {
				tailPart = parts[2]
			}
			handleSessionsPlaying(rec, r, database, tailPart)
			return
		}
		if r.Method == http.MethodGet && len(parts) == 2 && strings.EqualFold(parts[0], "Items") {
			handleItemDetail(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "Images") && strings.EqualFold(parts[3], "Primary") {
			handleItemPrimaryImage(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "Images") && strings.EqualFold(parts[3], "Logo") {
			handleItemLogoImage(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "Images") && strings.EqualFold(parts[3], "Backdrop") {
			handleItemBackdropImage(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Videos") && strings.EqualFold(parts[2], "stream") {
			handleVideoStream(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Videos") && strings.EqualFold(parts[2], "stream.m3u8") {
			handleVideoStreamM3U8(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "videos") && strings.HasPrefix(strings.ToLower(parts[2]), "original.") {
			handleVideoOriginal(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Items") && strings.EqualFold(parts[3], "Latest") {
			handleUserItemsLatest(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 4 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Items") {
			handleUserItemDetail(rec, r, database, parts[1], parts[3])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 3 && strings.EqualFold(parts[0], "Users") && strings.EqualFold(parts[2], "Items") {
			handleUserItems(rec, r, database, parts[1])
			return
		}
		if r.Method == http.MethodGet && len(parts) == 2 && strings.EqualFold(parts[0], "Users") {
			handleUserProfile(rec, r, database, parts[1])
			return
		}

		writeJSON(rec, http.StatusNotFound, map[string]any{
			"error":  "emby route not implemented yet",
			"path":   r.URL.Path,
			"method": r.Method,
		})
	})
}

func embyDebugEnabled() bool {
	return strings.TrimSpace(os.Getenv("MEOWFILM_DEBUG")) == "1"
}

func embyShouldSkipDebugLog(r *http.Request) bool {
	tail := strings.Trim(embyRouteTail(r), "/")
	parts := splitEmbyPath(tail)
	return len(parts) == 4 && strings.EqualFold(parts[0], "Items") && strings.EqualFold(parts[2], "Images")
}

func embyURL(r *http.Request) string {
	if r == nil || r.URL == nil {
		return ""
	}
	if strings.TrimSpace(r.URL.RawQuery) == "" {
		return r.URL.Path
	}
	return r.URL.Path + "?" + r.URL.RawQuery
}

func embyEscapedPath(r *http.Request) string {
	if r == nil || r.URL == nil {
		return ""
	}
	if strings.TrimSpace(r.URL.RawPath) != "" {
		return r.URL.RawPath
	}
	return r.URL.EscapedPath()
}

func embyRouteTail(r *http.Request) string {
	raw := embyEscapedPath(r)
	if strings.HasPrefix(raw, "/emby/") {
		return strings.TrimPrefix(raw, "/emby/")
	}
	return strings.TrimPrefix(raw, "/")
}

func splitEmbyPath(tail string) []string {
	clean := strings.Trim(path.Clean("/"+tail), "/")
	if clean == "" || clean == "." {
		return nil
	}
	return strings.Split(clean, "/")
}

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(payload)
}

type responseRecorder struct {
	http.ResponseWriter
	status int
	bytes  int
}

func (w *responseRecorder) WriteHeader(statusCode int) {
	if w.status == 0 {
		w.status = statusCode
	}
	w.ResponseWriter.WriteHeader(statusCode)
}

func (w *responseRecorder) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	n, err := w.ResponseWriter.Write(p)
	w.bytes += n
	return n, err
}

func (w *responseRecorder) Status() int {
	if w.status == 0 {
		return http.StatusOK
	}
	return w.status
}
