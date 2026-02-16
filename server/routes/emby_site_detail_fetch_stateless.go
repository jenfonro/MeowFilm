package routes

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
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
	detailRaw, err := catpawopen.RequestSpider(apiBase, sp, "detail", map[string]any{"id": vid})
	if err != nil {
		return nil, err
	}
	playFrom, playURL := catpawopen.ExtractDetailPlayFromURL(detailRaw)
	pans := catpawopen.ParsePlaySources(playFrom, playURL)
	if pans == nil {
		pans = []catpawopen.Pan{}
	}
	return pans, nil
}
