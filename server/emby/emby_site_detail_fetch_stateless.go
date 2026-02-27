package emby

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawopen"
)

func embyFetchSiteDetailPans(database *db.DB, u *embyUser, spiderAPI string, videoID string) ([]catpawopen.Pan, error) {
	if database == nil {
		return nil, errors.New("invalid database")
	}
	sp := strings.TrimSpace(spiderAPI)
	vid := strings.TrimSpace(videoID)
	if sp == "" || vid == "" {
		return nil, errors.New("invalid args")
	}
	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	detailRaw, err := cache.RequestSpiderDetailCached(apiBase, sp, vid)
	if err != nil {
		return nil, err
	}
	playFrom, playURL := catpawopen.ExtractDetailPlayFromURL(detailRaw)
	pans := catpawopen.ParsePlaySources(playFrom, playURL)
		if pans == nil {
			pans = []catpawopen.Pan{}
		}
		if embyIsPanMockEnabled(detailRaw) {
			pans, _ = embyResolvePanMockDetailPans(database, "", "", 0, nil, false, nil, nil, pans)
		}
		return pans, nil
	}
