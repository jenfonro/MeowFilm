package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

type embyDoubanHotItem = smart.DoubanHotItem

func embyDoubanAPIBase(database *db.DB) (base string, proxyBase string) {
	return smart.DoubanAPIBase(database)
}

func embyDoubanToProxiedURL(targetURL string, proxyBase string) string {
	return smart.DoubanToProxiedURL(targetURL, proxyBase)
}

func embyDoubanFetchRecentHot(database *db.DB, kind string, category string, hotType string, start int, limit int) ([]embyDoubanHotItem, error) {
	return smart.DoubanFetchRecentHot(database, kind, category, hotType, start, limit)
}
