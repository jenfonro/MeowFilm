package emby

import (
	"crypto/rand"
	"encoding/hex"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

var embyStreams = newEmbyStreamStore()

func handleEmbyPlaybackInfo(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, embyID string) {
	startAt := time.Now()
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	deviceID := embyClientDeviceID(r)
	mediaSourceID := embyComputeMediaSourceIDForItem(u.ID, deviceID, embyID)
	// Fast path: if we already have a recent 302 mapping for this user+device+item, reuse it and
	// avoid triggering smart resolution (search/detail/pick) again.
	if mediaSourceID != "" {
		if cachedURL, ok := embyStreams.Get(mediaSourceID); ok && strings.TrimSpace(cachedURL) != "" {
			embyNotePlayingProgress(u.ID, deviceID, embyPlayingGuardEntry{
				ExpireAt: time.Now().Add(embyPlayingGuardTTL),
				ItemID:   strings.TrimSpace(embyID),
			})
			container, containerList := embyDetectContainerFromURL(strings.TrimSpace(cachedURL))
			playSessionID := embyNewHexID()
			resp := embyBuildPlaybackInfoResponse(embyID, container, containerList, mediaSourceID, playSessionID)
			writeJSON(w, 200, resp)
			return
		}
	}
	parsed, ok := embyParseItemID(embyID)
	if !ok || parsed == nil {
		// Stateless site episodes: resolve via MeowFilm netdisk play (pan_mock) or catpawrunner play API.
		if siteVideoID, pan, epIndex, ok := embyParseSiteEpisodeIDV2(embyID); ok {
			urlPicked, headers, err := embyResolveStatelessSiteEpisodePlayback(database, u, siteVideoID, pan, epIndex)
			if err != nil {
				if embyDebugLogEnabled() {
					embyDebugPrintf("[emby][playback] fail item=%s err=%q cost=%s", embyID, err.Error(), time.Since(startAt).String())
				}
				embyBadGateway(w, err)
				return
			}

			container, containerList := embyDetectContainerFromURL(urlPicked)
			if embyDebugLogEnabled() {
				tvUser := ""
				if u != nil {
					tvUser = u.Username
				}
				embyDebugPrintf("[emby][playback] ok item=%s user=%s url=%q container=%s cost=%s", embyID, tvUser, urlPicked, container, time.Since(startAt).String())
			}
			if len(headers) != 0 {
				embyWriteError(w, 501, "该源需要自定义请求头，暂不支持")
				return
			}

			playSessionID := embyNewHexID()
			mediaSourceID := embyComputeMediaSourceIDForItem(u.ID, deviceID, embyID)
			embyNotePlayingProgress(u.ID, deviceID, embyPlayingGuardEntry{
				ExpireAt: time.Now().Add(embyPlayingGuardTTL),
				ItemID:   strings.TrimSpace(embyID),
			})
			embyStreams.Set(mediaSourceID, urlPicked, 60*time.Second)
			resp := embyBuildPlaybackInfoResponse(embyID, container, containerList, mediaSourceID, playSessionID)
			writeJSON(w, 200, resp)
			return
		}
		embyNotFound(w)
		return
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		embyWriteError(w, 400, "该条目不可播放")
		return
	}

	// Download-capability probing: within a short window after CanDownload detail checks,
	// avoid expensive smart resolution/search/detail work (user+device isolated).
	if parsed.TMDBID > 0 && embyIsDownloadProbeActive(u.ID, deviceID, parsed.TMDBID) {
		playSessionID := embyNewHexID()
		resp := embyBuildPlaybackInfoResponse(embyID, "mp4", "mp4", mediaSourceID, playSessionID)
		writeJSON(w, 200, resp)
		return
	}

	// Many clients prefetch PlaybackInfo (and sometimes do it repeatedly) even before the user actually
	// starts streaming. Do not trigger smart resolution here; defer it to the actual stream request
	// (/emby/media/... or /emby/Videos/.../stream) where we can cache the resulting 302 mapping.
	playSessionID := embyNewHexID()
	resp := embyBuildPlaybackInfoResponse(embyID, "mp4", "mkv,webm,mp4,m4v", mediaSourceID, playSessionID)
	writeJSON(w, 200, resp)
}

func embyNewHexID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
