package netdisk

import (
	"time"

	"github.com/jenfonro/meowfilm/server/cache"
)

const panAPIListCacheTTL = 60 * time.Second

type quarkListAPIValue struct {
	Vod     string
	ShareID string
}

type ucListAPIValue struct {
	Vod     string
	ShareID string
}

type baiduListAPIValue struct {
	Vod string
}

type yun139ListAPIValue struct {
	Vod string
}

type tianyi189ListAPIValue struct {
	Vod string
}

var (
	quarkListCache     = cache.NewTTLInflightCache[quarkListAPIValue](panAPIListCacheTTL, 4096)
	ucListCache        = cache.NewTTLInflightCache[ucListAPIValue](panAPIListCacheTTL, 4096)
	baiduListCache     = cache.NewTTLInflightCache[baiduListAPIValue](panAPIListCacheTTL, 4096)
	yun139ListCache    = cache.NewTTLInflightCache[yun139ListAPIValue](panAPIListCacheTTL, 4096)
	tianyi189ListCache = cache.NewTTLInflightCache[tianyi189ListAPIValue](panAPIListCacheTTL, 4096)
)
