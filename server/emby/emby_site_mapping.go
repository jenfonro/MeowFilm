package emby

import (
	"strings"
	"sync"
	"time"
)

type embySiteMapEntry struct {
	SiteKey   string
	SiteName  string
	SpiderAPI string
	VideoID   string
	Name      string
	Pic       string
	Remark    string
	ExpireAt  time.Time
}

var embySiteMap struct {
	mu sync.Mutex
	m  map[string]embySiteMapEntry // key: emitted item Id
}

func embyBuildSiteItemID(siteKey string, spiderAPI string, videoID string) string {
	sk := strings.TrimSpace(siteKey)
	sp := strings.TrimSpace(spiderAPI)
	vid := strings.TrimSpace(videoID)
	if sk == "" || sp == "" || vid == "" {
		return ""
	}
	return "site_" + embyStableHex32("site|"+sk+"|"+sp+"|"+vid)
}

func embyBuildSiteGroupItemID(groupKey string) string {
	gk := strings.TrimSpace(groupKey)
	if gk == "" {
		return ""
	}
	return "siteg_" + embyStableHex32("siteg|"+gk)
}

func embySiteMapPut(id string, e embySiteMapEntry, ttl time.Duration) {
	k := strings.TrimSpace(id)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	e.ExpireAt = time.Now().Add(ttl)

	embySiteMap.mu.Lock()
	defer embySiteMap.mu.Unlock()
	if embySiteMap.m == nil {
		embySiteMap.m = map[string]embySiteMapEntry{}
	}
	embySiteMap.m[k] = e
}

func embySiteMapGet(id string) (embySiteMapEntry, bool) {
	k := strings.TrimSpace(id)
	if k == "" {
		return embySiteMapEntry{}, false
	}
	now := time.Now()

	embySiteMap.mu.Lock()
	defer embySiteMap.mu.Unlock()
	if embySiteMap.m == nil {
		return embySiteMapEntry{}, false
	}
	e, ok := embySiteMap.m[k]
	if !ok {
		return embySiteMapEntry{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embySiteMap.m, k)
		return embySiteMapEntry{}, false
	}
	return e, true
}
