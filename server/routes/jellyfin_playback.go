package routes

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/url"
	"os"
	"path"
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
	startAt := time.Now()
	u, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if jellyfinDebugLogEnabled() {
		jellyfinDebugPrintf("[jellyfin][playback] start item=%s user=%s", jellyfinID, strings.TrimSpace(u.Username))
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

	forcedURL := strings.TrimSpace(os.Getenv("MEOWFILM_JELLYFIN_FORCE_PLAY_URL"))

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

	playURL := ""
	headers := map[string]string{}
	var err error
	if forcedURL != "" {
		playURL = forcedURL
		headers = map[string]string{}
		if jellyfinDebugLogEnabled() {
			jellyfinDebugPrintf("[jellyfin][playback] forced item=%s url=%q", jellyfinID, forcedURL)
		}
	} else {
		playURL, headers, err = jellyfinResolvePlaybackFromTMDB(database, u, parsed)
	}
	if err != nil {
		if jellyfinDebugLogEnabled() {
			jellyfinDebugPrintf("[jellyfin][playback] fail item=%s err=%q cost=%s", jellyfinID, err.Error(), time.Since(startAt).String())
		}
		jellyfinWriteError(w, 502, err.Error())
		return
	}
	originURL := strings.TrimSpace(playURL)
	finalURL := originURL
	finalHeaders := headers
	debugLog := strings.TrimSpace(os.Getenv("MEOWFILM_JELLYFIN_DEBUG_LOG")) == "1"
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

	usedProxy := "direct"
	playSessionID := jellyfinNewStreamID()
	streamID := jellyfinNewStreamID()
	if len(finalHeaders) > 0 {
		usedProxy = "stream"
		jellyfinStreams.Lock()
		jellyfinStreams.M[streamID] = jellyfinStreamSession{
			URL:     finalURL,
			Headers: finalHeaders,
			Expire:  time.Now().Add(20 * time.Minute),
		}
		jellyfinStreams.Unlock()

		base := jellyfinBaseURL(r)
		proxyURL := strings.TrimRight(base, "/") + "/jellyfin/stream/" + url.PathEscape(streamID)
		if tok := jellyfinReadToken(r); tok != "" {
			u2, _ := url.Parse(proxyURL)
			q2 := u2.Query()
			q2.Set("api_key", tok)
			u2.RawQuery = q2.Encode()
			proxyURL = u2.String()
		}
		finalURL = proxyURL
		finalHeaders = map[string]string{}
	}

	container := ""
	if u0, err := url.Parse(originURL); err == nil {
		ext := strings.ToLower(strings.TrimSpace(path.Ext(u0.Path)))
		if strings.HasPrefix(ext, ".") && len(ext) > 1 {
			container = ext[1:]
		}
	}
	if container == "" && jellyfinIsProbablyM3U8(originURL) {
		container = "m3u8"
	}
	if container == "" {
		container = "mp4"
	}

	if debugLog {
		jellyfinDebugPrintf("[jellyfin][playback] ok item=%s user=%s proxy=%s url=%q container=%s cost=%s", jellyfinID, tvUser, usedProxy, finalURL, container, time.Since(startAt).String())
	}

	mediaSourceID := streamID

	mediaStreams := []map[string]any{
		{
			"Codec":           "h264",
			"Index":           0,
			"IsDefault":       true,
			"IsForced":        false,
			"IsExternal":      false,
			"Type":            "Video",
			"IsInterlaced":    false,
			"BitRate":         0,
			"Width":           0,
			"Height":          0,
			"Level":           0,
			"Profile":         "",
			"PixelFormat":     "",
			"RefFrames":       0,
			"IsAnamorphic":    false,
			"Rotation":        0,
			"Language":        "",
			"DisplayTitle":    "",
			"DisplayLanguage": "",
		},
		{
			"Codec":           "aac",
			"Index":           1,
			"IsDefault":       true,
			"IsForced":        false,
			"IsExternal":      false,
			"Type":            "Audio",
			"Channels":        2,
			"SampleRate":      0,
			"BitRate":         0,
			"Language":        "",
			"DisplayTitle":    "",
			"DisplayLanguage": "",
		},
	}

	resp := map[string]any{
		"MediaSources": []map[string]any{
			{
				"Id":            mediaSourceID,
				"MediaSourceId": mediaSourceID,
				"Path":                       finalURL,
				"Protocol":                   "Http",
				"Type":                       "Default",
				"IsRemote":                   true,
				"Container":                  container,
				"Size":                       0,
				"RunTimeTicks":               0,
				"Bitrate":                    0,
				"IsInfiniteStream":           false,
				"ReadAtNativeFramerate":      false,
				"MediaStreams":               mediaStreams,
				"DefaultAudioStreamIndex":    1,
				"DefaultSubtitleStreamIndex": -1,
				"RequiredHttpHeaders":        map[string]string{},
				"RequiresOpening":            false,
				"RequiresClosing":            false,
				"RequiresLooping":            false,
				"SupportsDirectPlay":         true,
				"SupportsDirectStream":       true,
				"SupportsSeeking":            true,
				"SupportsProbing":            true,
				"SupportsTranscoding":        false,
				"VideoType":                  "VideoFile",
			},
		},
		"PlaySessionId":          playSessionID,
		"PlaybackStartTimeTicks": 0,
		"DirectPlayUrl":          finalURL,
		"DirectStreamUrl":        finalURL,

		"EnableDirectPlay":   true,
		"EnableDirectStream": true,
		"EnableTranscoding":  true,
	}

	if debugLog {
		var buf bytes.Buffer
		enc := json.NewEncoder(&buf)
		enc.SetEscapeHTML(false)
		if err := enc.Encode(resp); err == nil && buf.Len() > 0 {
			s := strings.TrimSpace(buf.String())
			if len(s) > 1800 {
				s = s[:1800] + "..."
			}
			jellyfinDebugPrintf("[jellyfin][playback] resp item=%s json=%s", jellyfinID, s)
		}
	}
	writeJSON(w, 200, resp)
}
