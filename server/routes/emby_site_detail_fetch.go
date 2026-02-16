package routes

import (
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
)

func embyLoadSiteDetailPans(database *db.DB, u *embyUser, seriesID string) ([]catpawopen.Pan, embySiteMapEntry, error) {
	if database == nil {
		return nil, embySiteMapEntry{}, errors.New("invalid database")
	}
	sid := strings.TrimSpace(seriesID)
	if sid == "" {
		return nil, embySiteMapEntry{}, errors.New("invalid series id")
	}
	siteEntry, ok := embySiteMapGet(sid)
	if !ok || strings.TrimSpace(siteEntry.SpiderAPI) == "" || strings.TrimSpace(siteEntry.VideoID) == "" {
		return nil, embySiteMapEntry{}, errors.New("site mapping missing")
	}
	if hit, ok := embySiteDetailCacheGet(sid); ok && hit.Pans != nil {
		return hit.Pans, siteEntry, nil
	}

	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return nil, embySiteMapEntry{}, errors.New("CatPawOpen 接口地址未设置")
	}

	detailRaw, err := catpawopen.RequestSpider(apiBase, strings.TrimSpace(siteEntry.SpiderAPI), "detail", map[string]any{"id": strings.TrimSpace(siteEntry.VideoID)})
	if err != nil {
		return nil, siteEntry, err
	}
	playFrom, playURL := catpawopen.ExtractDetailPlayFromURL(detailRaw)
	pans := catpawopen.ParsePlaySources(playFrom, playURL)
	if pans == nil {
		pans = []catpawopen.Pan{}
	}

	ttl := 30 * time.Minute
	embySiteDetailCachePut(sid, pans, ttl)

	// Populate season/episode maps for images and playback.
	pic := strings.TrimSpace(siteEntry.Pic)
	remark := strings.TrimSpace(siteEntry.Remark)
	siteKey := strings.TrimSpace(siteEntry.SiteKey)
	siteName := strings.TrimSpace(siteEntry.SiteName)
	spiderAPI := strings.TrimSpace(siteEntry.SpiderAPI)
	videoID := strings.TrimSpace(siteEntry.VideoID)

	for i, pan := range pans {
		seasonNo := i + 1
		label := embyNormalizePanDisplayLabel(pan.Label)
		if label == "" {
			label = "源" + intToCN(seasonNo)
		}
		seasonID := embyBuildSiteSeasonID(sid, seasonNo, label)
		if seasonID == "" {
			continue
		}
		embySiteSeasonMapPut(seasonID, embySiteSeasonMapEntry{
			SeriesID:  sid,
			SiteKey:   siteKey,
			SiteName:  siteName,
			SpiderAPI: spiderAPI,
			VideoID:   videoID,
			SeasonNo:  seasonNo,
			Label:     label,
			Pic:       pic,
			Remark:    remark,
		}, ttl)

		for j, ep := range pan.Episodes {
			epURL := strings.TrimSpace(ep.URL)
			if epURL == "" {
				continue
			}
			epID := embyBuildSiteEpisodeID(seasonID, j+1, epURL)
			if epID == "" {
				continue
			}
			embySiteEpisodeMapPut(epID, embySiteEpisodeMapEntry{
				SeriesID:       sid,
				SeasonID:       seasonID,
				SiteKey:        siteKey,
				SiteName:       siteName,
				SpiderAPI:      spiderAPI,
				VideoID:        videoID,
				SeasonNo:       seasonNo,
				EpisodeIndex:   j + 1,
				EpisodeName:    strings.TrimSpace(ep.Name),
				EpisodeURL:     epURL,
				EpisodePlayFlg: strings.TrimSpace(ep.Flag),
				Pic:            pic,
				Remark:         remark,
			}, ttl)
		}
	}

	return pans, siteEntry, nil
}
