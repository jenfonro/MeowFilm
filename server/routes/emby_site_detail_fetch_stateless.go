package routes

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyFetchSiteDetailPans(database *db.DB, u *embyUser, spiderAPI string, videoID string) ([]embyCatPan, error) {
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
	detailRaw, err := embyCatRequestSpider(apiBase, sp, "detail", map[string]any{"id": vid})
	if err != nil {
		return nil, err
	}
	playFrom, playURL := embyExtractDetailPlayFromURL(detailRaw)
	pans := embyParsePlaySources(playFrom, playURL)
	if pans == nil {
		pans = []embyCatPan{}
	}
	return pans, nil
}

