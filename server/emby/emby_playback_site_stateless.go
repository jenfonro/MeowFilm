package emby

import (
	"fmt"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func embyResolveStatelessSiteEpisodePlayback(database *db.DB, u *embyUser, siteVideoID int64, pan int, epIndex int) (finalURL string, finalHeaders map[string]string, err error) {
	if database == nil || siteVideoID <= 0 || pan <= 0 || epIndex <= 0 {
		return "", nil, errorString("invalid args")
	}

	sv, e := database.GetSiteVideoByID(siteVideoID)
	if e != nil {
		return "", nil, e
	}
	if sv == nil || strings.TrimSpace(sv.SiteKey) == "" || strings.TrimSpace(sv.VideoID) == "" {
		return "", nil, errorString("invalid site video")
	}

	spiderAPI := embyResolveSpiderAPIBySiteKey(database, strings.TrimSpace(sv.SiteKey))
	if strings.TrimSpace(spiderAPI) == "" {
		return "", nil, errorString("unknown spider api")
	}

	pans, e := embyFetchSiteDetailPansDedup(database, u, strings.TrimSpace(spiderAPI), strings.TrimSpace(sv.VideoID))
	if e != nil {
		return "", nil, e
	}
	panIdx := pan - 1
	if panIdx < 0 || panIdx >= len(pans) {
		return "", nil, errorString("invalid pan")
	}
	episodes := pans[panIdx].Episodes
	epIdx := epIndex - 1
	if epIdx < 0 || epIdx >= len(episodes) {
		return "", nil, errorString("invalid episode")
	}
	it := episodes[epIdx]
	epURL := strings.TrimSpace(it.URL)
	if epURL == "" {
		return "", nil, errorString("站点未返回可播放地址")
	}
	epFlag := strings.TrimSpace(it.Flag)
	if epFlag == "" {
		// Some spiders omit flag; treat as catpawrunner default.
		epFlag = ""
	}

	tvUser := ""
	if u != nil {
		tvUser = u.Username
	}

	urlPicked := ""
	headers := map[string]string{}
	providerForProxy := ""
	if pid := embyPanMockProviderFromLabel(strings.TrimSpace(epFlag)); pid != "" {
		providerForProxy = pid
		switch pid {
		case "189":
			ac := ""
			parts := strings.Split(strings.TrimSpace(epURL), "*")
			if len(parts) >= 2 {
				if v, ok := embyPanMock189AccessGet(strings.TrimSpace(parts[1])); ok {
					ac = v
				}
			}
			u2, _, _, _, e := netdisk.Tianyi189Play(database, strings.TrimSpace(epURL), strings.TrimSpace(ac))
			if e != nil {
				return "", nil, e
			}
			urlPicked = strings.TrimSpace(u2)
		case "quark":
			u2, header, e := netdisk.QuarkPlayWithTVUser(database, strings.TrimSpace(epURL), "", tvUser)
			if e != nil {
				return "", nil, e
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
			u2, header, e := netdisk.UCPlayWithTVUser(database, strings.TrimSpace(epURL), "", tvUser)
			if e != nil {
				return "", nil, e
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
			downloadURL, playURL, e := netdisk.Yun139Play(database, strings.TrimSpace(epFlag), strings.TrimSpace(epURL))
			u2 := strings.TrimSpace(downloadURL)
			if u2 == "" {
				u2 = strings.TrimSpace(playURL)
			}
			if e != nil {
				return "", nil, e
			}
			urlPicked = strings.TrimSpace(u2)
		case "baidu":
			u2, header, e := netdisk.BaiduPlay(database, strings.TrimSpace(epFlag), strings.TrimSpace(epURL), "/MeowFilm")
			if e != nil {
				return "", nil, e
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
			return "", nil, errorString("unsupported provider")
		}
	} else {
		apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
		if apiBase == "" {
			return "", nil, errorString("catpawrunner 接口地址未设置")
		}
		playPayload := map[string]any{
			"flag":    strings.TrimSpace(epFlag),
			"id":      strings.TrimSpace(epURL),
			"siteApi": strings.TrimSpace(spiderAPI),
		}
		if siteID := catpawrunner.ExtractSiteIDFromSpiderAPI(spiderAPI); siteID != "" {
			playPayload["siteId"] = siteID
		}
		playRaw, e := catpawrunner.RequestPlay(apiBase, tvUser, playPayload)
		if e != nil {
			return "", nil, e
		}
		urlPicked = strings.TrimSpace(catpawrunner.PickFirstPlayableURL(playRaw))
		if urlPicked == "" {
			return "", nil, errorString("站点未返回可播放地址")
		}
		urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
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
		return "", nil, errorString("站点未返回可播放地址: " + fmt.Sprintf("site=%s video=%d pan=%d ep=%d", strings.TrimSpace(sv.SiteKey), siteVideoID, pan, epIndex))
	}
	urlPicked, headers = embyProxyIfNeeded(database, u, providerForProxy, strings.TrimSpace(urlPicked), headers)
	if len(headers) == 0 {
		return strings.TrimSpace(urlPicked), nil, nil
	}
	return strings.TrimSpace(urlPicked), headers, nil
}
