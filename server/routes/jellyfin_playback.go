package routes

import (
	"crypto/rand"
	"encoding/base64"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type jellyfinStreamSession struct {
	URL     string
	Headers map[string]string
	Expire  time.Time
}

var jellyfinStreams = struct {
	sync.Mutex
	M map[string]jellyfinStreamSession
}{
	M: map[string]jellyfinStreamSession{},
}

func jellyfinNewStreamID() string {
	b := make([]byte, 18)
	_, _ = rand.Read(b)
	return base64.RawURLEncoding.EncodeToString(b)
}

func handleJellyfinPlaybackInfo(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, jellyfinID string) {
	u, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	parsed, ok := jellyfinParseItemID(jellyfinID)
	if !ok || parsed == nil {
		http.NotFound(w, r)
		return
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		jellyfinWriteError(w, 400, "该条目不可播放")
		return
	}

	// For Douban IDs (movie/series), resolve to TMDB before selecting playback.
	if parsed.Source == "douban" && parsed.TMDBID <= 0 && parsed.DoubanID != "" {
		m, _ := jellyfinGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
		title := ""
		year := 0
		if m != nil {
			title = m.Title
			year = m.Year
		}
		tid, err := jellyfinResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
		if err != nil {
			jellyfinWriteError(w, 502, err.Error())
			return
		}
		if tid <= 0 {
			jellyfinWriteError(w, 404, "TMDB 未匹配")
			return
		}
		parsed.Source = "tmdb"
		parsed.TMDBID = tid
	}

	playURL, headers, err := jellyfinResolvePlaybackFromTMDB(database, u, parsed)
	if err != nil {
		jellyfinWriteError(w, 502, err.Error())
		return
	}
	finalURL := strings.TrimSpace(playURL)
	finalHeaders := headers
	tvUser := ""
	if u != nil {
		tvUser = u.Username
	}
	apiBase := jellyfinResolveCatApiBaseForUser(database, u)

	// If headers are present, try to eliminate them for Infuse:
	// - For m3u8: register CatPawOpen m3u8 proxy playlist and return proxy URL (headers cleared).
	// - For non-m3u8: proxy through MeowFilm /jellyfin/stream/{id}.
	if len(finalHeaders) > 0 && jellyfinIsProbablyM3U8(finalURL) && apiBase != "" {
		_, proxyURL, err := jellyfinRegisterCatM3U8(apiBase, tvUser, finalURL, finalHeaders)
		if err == nil && strings.TrimSpace(proxyURL) != "" {
			finalURL = strings.TrimSpace(proxyURL)
			finalHeaders = map[string]string{}
		}
	}

	mediaPath := finalURL
	if len(finalHeaders) > 0 {
		streamID := jellyfinNewStreamID()
		jellyfinStreams.Lock()
		jellyfinStreams.M[streamID] = jellyfinStreamSession{
			URL:     finalURL,
			Headers: finalHeaders,
			Expire:  time.Now().Add(20 * time.Minute),
		}
		jellyfinStreams.Unlock()

		base := jellyfinBaseURL(r)
		if base == "" {
			base = ""
		}
		mediaPath = strings.TrimRight(base, "/") + "/jellyfin/stream/" + url.PathEscape(streamID)
		// pass token via api_key to keep it simple for clients.
		if tok := jellyfinReadToken(r); tok != "" {
			u2, _ := url.Parse(mediaPath)
			q := u2.Query()
			q.Set("api_key", tok)
			u2.RawQuery = q.Encode()
			mediaPath = u2.String()
		}
	}

	writeJSON(w, 200, map[string]any{
		"MediaSources": []map[string]any{
			{
				"Id":                   jellyfinNewStreamID(),
				"Path":                 mediaPath,
				"Protocol":             "Http",
				"Type":                 "Default",
				"SupportsDirectPlay":   true,
				"SupportsDirectStream": true,
			},
		},
		"PlaySessionId": jellyfinNewStreamID(),
	})
}
