package emby

import (
	"fmt"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/goproxy"
)

func handleEmbyVideos(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) < 2 {
		embyNotFound(w)
		return
	}

	itemID := strings.TrimSpace(parts[0])
	action := strings.ToLower(strings.TrimSpace(parts[1]))
	if itemID == "" {
		embyNotFound(w)
		return
	}

	if action == "stream" || strings.HasPrefix(action, "stream.") || strings.HasSuffix(action, ".m3u8") {
		handleEmbyVideoStream(w, r, database, serverID, itemID)
		return
	}
	embyNotFound(w)
}

func handleEmbyVideoStream(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, itemID string) {
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		embyMethodNotAllowed(w)
		return
	}

	deviceID := embyClientDeviceID(r)
	computedMediaSourceID := embyComputeMediaSourceIDForItem(u.ID, deviceID, itemID)
	mediaSourceID := embyQueryTrimCI(r, "MediaSourceId")
	// Some clients omit MediaSourceId. Try the deterministic id derived from user+device+item
	// so we can reuse the short-lived 302 mapping without re-resolving playback.
	if strings.TrimSpace(mediaSourceID) == "" {
		mediaSourceID = computedMediaSourceID
	}

	if mediaSourceID != "" {
		if u0, ok := embyStreams.Get(mediaSourceID); ok && strings.TrimSpace(u0) != "" {
			http.Redirect(w, r, strings.TrimSpace(u0), http.StatusFound)
			return
		}
	}
	// Some clients send a MediaSourceId that doesn't match our deterministic scheme.
	// Fall back to the computed id as well.
	if computedMediaSourceID != "" && computedMediaSourceID != mediaSourceID {
		if u0, ok := embyStreams.Get(computedMediaSourceID); ok && strings.TrimSpace(u0) != "" {
			http.Redirect(w, r, strings.TrimSpace(u0), http.StatusFound)
			return
		}
	}

	resolveKey := computedMediaSourceID
	if strings.TrimSpace(resolveKey) == "" {
		resolveKey = mediaSourceID
	}
	if strings.TrimSpace(resolveKey) == "" {
		// Last resort: still dedupe within the same process for the same user+device+item.
		resolveKey = strings.TrimSpace(u.ID) + "|" + strings.TrimSpace(deviceID) + "|" + strings.TrimSpace(itemID)
	}

	parsed, ok := embyParseItemID(itemID)
	if !ok || parsed == nil {
		// Stateless site episodes: resolve on demand and cache the resulting 302 mapping.
		if siteVideoID, pan, epIndex, ok := embyParseSiteEpisodeIDV2(itemID); ok {
			finalURL, err := embyResolveStreamOnce(resolveKey, func() (string, error) {
				playURL, headers, err := embyResolveStatelessSiteEpisodePlayback(database, u, siteVideoID, pan, epIndex)
				if err != nil {
					return "", err
				}
				u0 := strings.TrimSpace(playURL)
				h0 := headers
				if len(h0) != 0 {
					if pickedURL, ok, err2 := goproxy.ProxyIfNeeded(database, "", u0, h0); err2 == nil && ok && strings.TrimSpace(pickedURL) != "" {
						u0 = strings.TrimSpace(pickedURL)
						h0 = nil
					}
				}
				if len(h0) != 0 {
					return "", fmt.Errorf("该源需要自定义请求头，暂不支持")
				}
				if u0 == "" {
					return "", fmt.Errorf("站点未返回可播放地址")
				}
				return u0, nil
			})
			if err != nil {
				embyBadGateway(w, err)
				return
			}
			// Cache and respond.
			if mediaSourceID != "" {
				embyStreams.Set(mediaSourceID, strings.TrimSpace(finalURL), 60*time.Second)
			}
			if computedMediaSourceID != "" && computedMediaSourceID != mediaSourceID {
				embyStreams.Set(computedMediaSourceID, strings.TrimSpace(finalURL), 60*time.Second)
			}
			// If the client requests an HLS manifest, return a minimal single-segment playlist.
			if strings.HasSuffix(strings.ToLower(strings.TrimSpace(r.URL.Path)), ".m3u8") {
				w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
				w.WriteHeader(http.StatusOK)
				_, _ = fmt.Fprintf(w, "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3600\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:3600.0,\n%s\n#EXT-X-ENDLIST\n", finalURL)
				return
			}
			http.Redirect(w, r, strings.TrimSpace(finalURL), http.StatusFound)
			return
		}
		embyNotFound(w)
		return
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		embyWriteError(w, 400, "该条目不可播放")
		return
	}

	p := *parsed
	if err := embyNormalizeParsedToTMDB(database, &p, true); err != nil {
		embyBadGateway(w, err)
		return
	}

	var pickedMeta *smartPlaybackPickedMeta
	finalURL, err := embyResolveStreamOnce(resolveKey, func() (string, error) {
		playURL, headers, picked, err := embyResolvePlaybackFromTMDB(database, u, &p)
		if err != nil {
			return "", err
		}
		pickedMeta = picked
		u0 := strings.TrimSpace(playURL)
		h0 := headers
		if len(h0) != 0 {
			// Best-effort: register to GoProxy and return a header-free proxy URL for Emby clients.
			provider := ""
			if picked != nil {
				provider = strings.TrimSpace(picked.Provider)
			}
			if pickedURL, ok, err2 := goproxy.ProxyIfNeeded(database, provider, u0, h0); err2 == nil && ok && strings.TrimSpace(pickedURL) != "" {
				u0 = strings.TrimSpace(pickedURL)
				h0 = nil
			}
		}
		if len(h0) != 0 {
			return "", fmt.Errorf("该源需要自定义请求头，暂不支持")
		}
		if u0 == "" {
			return "", fmt.Errorf("无可用播放地址")
		}
		return u0, nil
	})
	if err != nil {
		embyBadGateway(w, err)
		return
	}
	// Cache the resolved playback URL so subsequent PlaybackInfo/stream calls can avoid
	// triggering smart search/detail/pick again.
	meta := embyStreamMeta{}
	if pickedMeta != nil {
		meta = embyStreamMeta{
			SiteKey:  strings.TrimSpace(pickedMeta.SiteKey),
			VideoID:  strings.TrimSpace(pickedMeta.VideoID),
			PanLabel: strings.TrimSpace(pickedMeta.PanFlag),
			Provider: strings.TrimSpace(pickedMeta.Provider),
		}
	}
	if mediaSourceID != "" {
		if strings.TrimSpace(meta.SiteKey) != "" && strings.TrimSpace(meta.VideoID) != "" {
			embyStreams.SetMeta(mediaSourceID, strings.TrimSpace(finalURL), 60*time.Second, meta)
		} else {
			embyStreams.Set(mediaSourceID, strings.TrimSpace(finalURL), 60*time.Second)
		}
	}
	if computedMediaSourceID != "" && computedMediaSourceID != mediaSourceID {
		if strings.TrimSpace(meta.SiteKey) != "" && strings.TrimSpace(meta.VideoID) != "" {
			embyStreams.SetMeta(computedMediaSourceID, strings.TrimSpace(finalURL), 60*time.Second, meta)
		} else {
			embyStreams.Set(computedMediaSourceID, strings.TrimSpace(finalURL), 60*time.Second)
		}
	}
	http.Redirect(w, r, strings.TrimSpace(finalURL), http.StatusFound)
}
