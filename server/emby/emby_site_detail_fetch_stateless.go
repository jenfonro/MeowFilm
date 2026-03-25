package emby

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func embyFetchSiteDetailPans(database *db.DB, u *embyUser, spiderAPI string, siteDetail string) ([]catpawrunner.Pan, error) {
	if database == nil {
		return nil, errors.New("invalid database")
	}
	sp := strings.TrimSpace(spiderAPI)
	detail := strings.TrimSpace(siteDetail)
	if sp == "" || detail == "" {
		return nil, errors.New("invalid args")
	}
	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return nil, errors.New("catpawrunner 接口地址未设置")
	}
	detailRaw, err := cache.RequestSpiderDetailCached(apiBase, sp, detail)
	if err != nil {
		return nil, err
	}
	playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
	pans := catpawrunner.ParsePlaySources(playFrom, playURL)
	if pans == nil {
		pans = []catpawrunner.Pan{}
	}
	if smartIsPanMockEnabled(detailRaw) {
		for i := range pans {
			pans[i].PanMockEnabled = true
		}
		pans, _ = embyResolvePanMockDetailPans(database, "", "", 0, nil, false, nil, nil, pans)
	}
	return pans, nil
}
