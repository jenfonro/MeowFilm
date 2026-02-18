package emby

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawopen"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

var embyStreams = newEmbyStreamStore()

type embyPlaybackResolved struct {
	URL     string
	Headers map[string]string
}

var embyPlaybackResolveDedup = cache.NewTTLInflightCache[embyPlaybackResolved](15*time.Second, 4096)

func handleEmbyPlaybackInfo(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, embyID string) {
	startAt := time.Now()
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if embyDebugLogEnabled() {
		embyDebugPrintf("[emby][playback] start item=%s user=%s", embyID, strings.TrimSpace(u.Username))
	}
	parsed, ok := embyParseItemID(embyID)
	if !ok || parsed == nil {
		// Stateless site episodes: resolve via MeowFilm netdisk play (pan_mock) or CatPawOpen play API.
		if ep, ok := embyDecodeSiteEpisodeID(embyID); ok {
			tvUser := ""
			if u != nil {
				tvUser = u.Username
			}
			urlPicked := ""
			headers := map[string]string{}
			if pid := smartPanMockProviderID(strings.TrimSpace(ep.Flag)); pid != "" {
				switch pid {
				case "tianyi":
					ac := ""
					parts := strings.Split(strings.TrimSpace(ep.URL), "*")
					if len(parts) >= 2 {
						if v, ok := embyPanMock189AccessGet(strings.TrimSpace(parts[1])); ok {
							ac = v
						}
					}
					u2, _, _, _, err := netdisk.Tianyi189Play(database, strings.TrimSpace(ep.URL), strings.TrimSpace(ac))
					if err != nil {
						if embyDebugLogEnabled() {
							embyDebugPrintf("[emby][playback] fail item=%s pid=%s err=%q cost=%s", embyID, pid, err.Error(), time.Since(startAt).String())
						}
						embyBadGateway(w, err)
						return
					}
					urlPicked = strings.TrimSpace(u2)
				case "quark":
					u2, header, err := netdisk.QuarkPlay(database, strings.TrimSpace(ep.URL), "")
					if err != nil {
						if embyDebugLogEnabled() {
							embyDebugPrintf("[emby][playback] fail item=%s pid=%s err=%q cost=%s", embyID, pid, err.Error(), time.Since(startAt).String())
						}
						embyBadGateway(w, err)
						return
					}
					urlPicked = strings.TrimSpace(u2)
					if header != nil {
						for k, v := range header {
							kk := strings.TrimSpace(k)
							sv := strings.TrimSpace(v)
							if kk != "" && sv != "" {
								headers[kk] = sv
							}
						}
					}
				case "uc":
					u2, header, err := netdisk.UCPlay(database, strings.TrimSpace(ep.URL), "")
					if err != nil {
						if embyDebugLogEnabled() {
							embyDebugPrintf("[emby][playback] fail item=%s pid=%s err=%q cost=%s", embyID, pid, err.Error(), time.Since(startAt).String())
						}
						embyBadGateway(w, err)
						return
					}
					urlPicked = strings.TrimSpace(u2)
					if header != nil {
						for k, v := range header {
							kk := strings.TrimSpace(k)
							sv := strings.TrimSpace(v)
							if kk != "" && sv != "" {
								headers[kk] = sv
							}
						}
					}
				case "139":
					downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(ep.Flag), strings.TrimSpace(ep.URL))
					u2 := strings.TrimSpace(downloadURL)
					if u2 == "" {
						u2 = strings.TrimSpace(playURL)
					}
					if err != nil {
						if embyDebugLogEnabled() {
							embyDebugPrintf("[emby][playback] fail item=%s pid=%s err=%q cost=%s", embyID, pid, err.Error(), time.Since(startAt).String())
						}
						embyBadGateway(w, err)
						return
					}
					urlPicked = strings.TrimSpace(u2)
				case "baidu":
					u2, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(ep.Flag), strings.TrimSpace(ep.URL), "/MeowFilm")
					if err != nil {
						if embyDebugLogEnabled() {
							embyDebugPrintf("[emby][playback] fail item=%s pid=%s err=%q cost=%s", embyID, pid, err.Error(), time.Since(startAt).String())
						}
						embyBadGateway(w, err)
						return
					}
					urlPicked = strings.TrimSpace(u2)
					if header != nil {
						for k, v := range header {
							kk := strings.TrimSpace(k)
							sv := strings.TrimSpace(v)
							if kk != "" && sv != "" {
								headers[kk] = sv
							}
						}
					}
				default:
					embyNotFound(w)
					return
				}
			} else {
				apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
				if apiBase == "" {
					embyWriteError(w, 502, "CatPawOpen 接口地址未设置")
					return
				}
				playPayload := map[string]any{
					"flag":    strings.TrimSpace(ep.Flag),
					"id":      strings.TrimSpace(ep.URL),
					"siteApi": strings.TrimSpace(ep.SiteAPI),
				}
				if siteID := catpawopen.ExtractSiteIDFromSpiderAPI(ep.SiteAPI); siteID != "" {
					playPayload["siteId"] = siteID
				}
				playRaw, err := catpawopen.RequestPlay(apiBase, tvUser, playPayload)
				if err != nil {
					if embyDebugLogEnabled() {
						embyDebugPrintf("[emby][playback] fail item=%s err=%q cost=%s", embyID, err.Error(), time.Since(startAt).String())
					}
					embyBadGateway(w, err)
					return
				}
				urlPicked = strings.TrimSpace(catpawopen.PickFirstPlayableURL(playRaw))
				if urlPicked == "" {
					embyWriteError(w, 502, "站点未返回可播放地址")
					return
				}
				urlPicked = catpawopen.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
				if h, ok := playRaw["header"].(map[string]any); ok {
					for k, v := range h {
						kk := strings.TrimSpace(k)
						if kk == "" {
							continue
						}
						sv := strings.TrimSpace(embyAnyToString(v))
						if sv == "" {
							continue
						}
						headers[kk] = sv
					}
				}
			}
			if strings.TrimSpace(urlPicked) == "" {
				embyWriteError(w, 502, "站点未返回可播放地址")
				return
			}

			container, containerList := embyDetectContainerFromURL(urlPicked)
			if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][playback] ok item=%s user=%s url=%q container=%s cost=%s", embyID, tvUser, urlPicked, container, time.Since(startAt).String())
			}
			if len(headers) != 0 {
				embyWriteError(w, 501, "该源需要自定义请求头，暂不支持")
				return
			}

			playSessionID := embyNewHexID()
			mediaSourceID := embyStableHex32(embyID)
			embyStreams.Set(mediaSourceID, urlPicked, 20*time.Minute)
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

	tvUser := ""
	if u != nil {
		tvUser = u.Username
	}
	key := strings.TrimSpace(serverID) + "|" + strings.TrimSpace(u.ID) + "|" + strings.TrimSpace(embyID)
	res, _, err := embyPlaybackResolveDedup.DoWithOptions(key, cache.TTLInflightCacheOptions{
		Sliding:      true,
		MaxLife:      1 * time.Minute,
		MaxRefreshes: 5,
		CacheErrors:  false,
	}, func() (embyPlaybackResolved, error) {
		if parsed == nil {
			return embyPlaybackResolved{}, errorString("invalid item id")
		}
		p := *parsed

		// For Douban IDs (movie/series), resolve to TMDB before selecting playback.
		if p.Source == "douban" && p.TMDBID <= 0 && strings.TrimSpace(p.DoubanID) != "" {
			m, _ := embyGetDoubanTMDBMap(database, p.Kind, p.DoubanID)
			title := ""
			year := 0
			if m != nil {
				title = m.Title
				year = m.Year
			}
			tid, err := embyResolveTMDBForDouban(database, p.Kind, p.DoubanID, title, year)
			if err != nil {
				return embyPlaybackResolved{}, err
			}
			if tid <= 0 {
				return embyPlaybackResolved{}, errorString("TMDB 未匹配")
			}
			p.Source = "tmdb"
			p.TMDBID = tid
		}

		playURL, headers, err := embyResolvePlaybackFromTMDB(database, u, &p)
		if err != nil {
			return embyPlaybackResolved{}, err
		}
		out := embyPlaybackResolved{URL: strings.TrimSpace(playURL)}
		if headers != nil && len(headers) > 0 {
			cp := make(map[string]string, len(headers))
			for k, v := range headers {
				kk := strings.TrimSpace(k)
				vv := strings.TrimSpace(v)
				if kk != "" && vv != "" {
					cp[kk] = vv
				}
			}
			out.Headers = cp
		}
		return out, nil
	})
	if err != nil {
		if embyDebugLogEnabled() {
			embyDebugPrintf("[emby][playback] fail item=%s err=%q cost=%s", embyID, err.Error(), time.Since(startAt).String())
		}
		embyBadGateway(w, err)
		return
	}
	finalURL := strings.TrimSpace(res.URL)
	finalHeaders := res.Headers
	debugLog := embyDebugLogEnabled()
	playSessionID := embyNewHexID()
	mediaSourceID := embyStableHex32(embyID)

	container, containerList := embyDetectContainerFromURL(finalURL)

	if debugLog {
		embyDebugPrintf("[emby][playback] ok item=%s user=%s url=%q container=%s cost=%s", embyID, tvUser, finalURL, container, time.Since(startAt).String())
	}

	if len(finalHeaders) != 0 {
		embyWriteError(w, 501, "该源需要自定义请求头，暂不支持")
		return
	}

	// Playback is served via /Videos/{itemId}/stream.* using MediaSourceId.
	embyStreams.Set(mediaSourceID, finalURL, 20*time.Minute)

	resp := embyBuildPlaybackInfoResponse(embyID, container, containerList, mediaSourceID, playSessionID)

	if debugLog {
		var buf bytes.Buffer
		enc := json.NewEncoder(&buf)
		enc.SetEscapeHTML(false)
		if err := enc.Encode(resp); err == nil && buf.Len() > 0 {
			s := strings.TrimSpace(buf.String())
			if len(s) > 1800 {
				s = s[:1800] + "..."
			}
			embyDebugPrintf("[emby][playback] resp item=%s json=%s", embyID, s)
		}
	}
	writeJSON(w, 200, resp)
}

func embyNewHexID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
