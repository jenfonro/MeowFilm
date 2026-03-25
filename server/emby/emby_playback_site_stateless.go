package emby

import (
	"fmt"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyResolveStatelessSiteEpisodePlaybackPayload(database *db.DB, u *embyUser, siteVideoID int64, pan int, epIndex int) (payload map[string]any, provider string, err error) {
	if database == nil || siteVideoID <= 0 || pan <= 0 || epIndex <= 0 {
		return nil, "", errorString("invalid args")
	}

	sv, e := database.GetSiteVideoByID(siteVideoID)
	if e != nil {
		return nil, "", e
	}
	if sv == nil || strings.TrimSpace(sv.SiteKey) == "" || strings.TrimSpace(sv.SiteDetail) == "" {
		return nil, "", errorString("invalid site video")
	}

	spiderAPI := embyResolveSpiderAPIBySiteKey(database, strings.TrimSpace(sv.SiteKey))
	if strings.TrimSpace(spiderAPI) == "" {
		return nil, "", errorString("unknown spider api")
	}

	pans, e := embyFetchSiteDetailPansDedup(database, u, strings.TrimSpace(spiderAPI), strings.TrimSpace(sv.SiteDetail))
	if e != nil {
		return nil, "", e
	}
	panIdx := pan - 1
	if panIdx < 0 || panIdx >= len(pans) {
		return nil, "", errorString("invalid pan")
	}
	episodes := pans[panIdx].Episodes
	epIdx := epIndex - 1
	if epIdx < 0 || epIdx >= len(episodes) {
		return nil, "", errorString("invalid episode")
	}
	it := episodes[epIdx]
	epURL := strings.TrimSpace(it.URL)
	if epURL == "" {
		return nil, "", errorString("站点未返回可播放地址")
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
		playPayload, e := smart.ResolvePanProviderPlaybackPayload(
			database,
			smartUserFromEmby(u),
			pid,
			strings.TrimSpace(epFlag),
			strings.TrimSpace(epURL),
			nil,
			"/MeowFilm",
		)
		if e != nil {
			return nil, "", e
		}
		urlPicked, headers = netdisk.PlayPayloadURLHeaders(playPayload)
		if urlPicked == "" {
			return nil, "", errorString("unsupported provider")
		}
	} else {
		apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
		if apiBase == "" {
			return nil, "", errorString("catpawrunner 接口地址未设置")
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
			return nil, "", e
		}
		payloadOut := smart.BuildCatpawPlayPayload(playRaw, apiBase, tvUser)
		urlPicked, headers = netdisk.PlayPayloadURLHeaders(payloadOut)
		if urlPicked == "" {
			return nil, "", errorString("站点未返回可播放地址")
		}
		return payloadOut, providerForProxy, nil
	}

	if strings.TrimSpace(urlPicked) == "" {
		return nil, "", errorString("站点未返回可播放地址: " + fmt.Sprintf("site=%s video=%d pan=%d ep=%d", strings.TrimSpace(sv.SiteKey), siteVideoID, pan, epIndex))
	}
	return netdisk.BuildPlayPayload(strings.TrimSpace(urlPicked), headers), providerForProxy, nil
}
