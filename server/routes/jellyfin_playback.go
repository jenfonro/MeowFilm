package routes

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
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
	URL    string
	Expire time.Time
}

var jellyfinStreams = struct {
	sync.Mutex
	M map[string]jellyfinStreamSession
}{
	M: map[string]jellyfinStreamSession{},
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
	playSessionID := jellyfinNewHexID()
	mediaSourceID := jellyfinStableHex32(jellyfinID)

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
		jellyfinDebugPrintf("[jellyfin][playback] ok item=%s user=%s url=%q container=%s cost=%s", jellyfinID, tvUser, finalURL, container, time.Since(startAt).String())
	}

	if len(finalHeaders) != 0 {
		jellyfinWriteError(w, 501, "该源需要自定义请求头，暂不支持")
		return
	}

	etag := mediaSourceID

	// Playback is served via /Videos/{itemId}/stream.* using MediaSourceId.
	jellyfinStreams.Lock()
	jellyfinStreams.M[mediaSourceID] = jellyfinStreamSession{
		URL:    finalURL,
		Expire: time.Now().Add(20 * time.Minute),
	}
	jellyfinStreams.Unlock()

	containerList := container
	switch container {
	case "mkv", "webm":
		containerList = "mkv,webm"
	case "mp4", "m4v":
		containerList = "mp4,m4v"
	}

	mediaStreams := []map[string]any{
		{
			"Codec":                  "h264",
			"Index":                  0,
			"Type":                   "Video",
			"IsDefault":              true,
			"IsForced":               false,
			"IsExternal":             false,
			"IsInterlaced":           false,
			"RefFrames":              1,
			"AverageFrameRate":       0,
			"RealFrameRate":          0,
			"CodecTimeBase":          "",
			"VideoRange":             "",
			"Language":               "",
			"DisplayTitle":           "",
			"Height":                 0,
			"Width":                  0,
			"BitRate":                0,
			"Profile":                "",
			"Level":                  0,
			"PixelFormat":            "",
			"AspectRatio":            "",
			"IsTextSubtitleStream":   false,
			"SupportsExternalStream": false,
			"TimeBase":               "1/1000",
		},
		{
			"Codec":                  "aac",
			"Index":                  1,
			"Type":                   "Audio",
			"IsDefault":              true,
			"IsForced":               false,
			"IsExternal":             false,
			"IsInterlaced":           false,
			"Language":               "",
			"DisplayTitle":           "",
			"Channels":               2,
			"SampleRate":             0,
			"BitRate":                0,
			"CodecTimeBase":          "",
			"SupportsExternalStream": false,
			"IsTextSubtitleStream":   false,
			"TimeBase":               "1/1000",
			"Level":                  0,
		},
	}

	resp := map[string]any{
		"MediaSources": []map[string]any{
			{
				"MediaAttachments": []any{},
				"RunTimeTicks":     0,
				"RequiresLooping":  false,
				"MediaStreams":     mediaStreams,
				"RequiresOpening":  false,

				"Path":          "/jellyfin/media/" + url.PathEscape(jellyfinID) + "." + url.PathEscape(container),
				"ETag":          etag,
				"Name":          jellyfinID,
				"Id":            mediaSourceID,
				"MediaSourceId": mediaSourceID,
				"Type":          "Default",
				"Size":          0,
				"Bitrate":       0,

				"SupportsDirectPlay":   true,
				"SupportsDirectStream": true,
				"SupportsProbing":      true,
				"SupportsTranscoding":  true,

				"RequiresClosing":            false,
				"Formats":                    []any{},
				"RequiredHttpHeaders":        map[string]string{},
				"IsRemote":                   false,
				"IgnoreIndex":                false,
				"IsInfiniteStream":           false,
				"IgnoreDts":                  false,
				"Container":                  containerList,
				"VideoType":                  "VideoFile",
				"DefaultAudioStreamIndex":    1,
				"DefaultSubtitleStreamIndex": -1,
				"GenPtsInput":                false,
				"ReadAtNativeFramerate":      false,
				"Protocol":                   "File",
			},
		},
		"PlaySessionId": playSessionID,
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

func jellyfinNewHexID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
