package netdisk

import (
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/cache"
)

type listCache2 struct {
	Vod     string
	ShareID string
}

type listCache3 struct {
	Vod       string
	ShareID   string
	ShareCode string
}

const netdiskListCacheTTL = 5 * time.Minute
const netdiskListNegCacheTTL = 1 * time.Minute

var (
	quarkListCacheTwoTier = cache.NewTwoTierTTLInflightCache[listCache2](netdiskListCacheTTL, 256, netdiskListNegCacheTTL, 1024)
	ucListCacheTwoTier    = cache.NewTwoTierTTLInflightCache[listCache2](netdiskListCacheTTL, 256, netdiskListNegCacheTTL, 1024)
	y139ListCacheTwoTier  = cache.NewTwoTierTTLInflightCache[listCache2](netdiskListCacheTTL, 256, netdiskListNegCacheTTL, 1024)
	baiduListCacheTwoTier = cache.NewTwoTierTTLInflightCache[listCache2](netdiskListCacheTTL, 256, netdiskListNegCacheTTL, 1024)
	t189ListCacheTwoTier  = cache.NewTwoTierTTLInflightCache[listCache3](netdiskListCacheTTL, 256, netdiskListNegCacheTTL, 1024)
)

func listCacheKey(prefix string, parts ...string) string {
	p := strings.TrimSpace(prefix)
	if p == "" {
		p = "list"
	}
	sb := strings.Builder{}
	sb.WriteString(p)
	for _, it := range parts {
		sb.WriteString("|")
		sb.WriteString(strings.TrimSpace(it))
	}
	return sb.String()
}

func listCacheCredentialPart(raw string) string {
	p := strings.TrimSpace(raw)
	if p == "" {
		return "cred:none"
	}
	return "cred:provided:" + p
}
