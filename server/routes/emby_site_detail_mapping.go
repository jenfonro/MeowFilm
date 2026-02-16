package routes

import (
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawopen"
)

type embySiteDetailCacheEntry struct {
	Pans     []catpawopen.Pan
	ExpireAt time.Time
}

type embySiteSeasonMapEntry struct {
	SeriesID  string
	SiteKey   string
	SiteName  string
	SpiderAPI string
	VideoID   string

	SeasonNo int
	Label    string
	Pic      string
	Remark   string

	ExpireAt time.Time
}

type embySiteEpisodeMapEntry struct {
	SeriesID  string
	SeasonID  string
	SiteKey   string
	SiteName  string
	SpiderAPI string
	VideoID   string

	SeasonNo       int
	EpisodeIndex   int
	EpisodeName    string
	EpisodeURL     string
	EpisodePlayFlg string

	Pic      string
	Remark   string
	ExpireAt time.Time
}

var embySiteDetailCache struct {
	mu sync.Mutex
	m  map[string]embySiteDetailCacheEntry // key: series item Id
}

var embySiteSeasonMap struct {
	mu sync.Mutex
	m  map[string]embySiteSeasonMapEntry // key: emitted season Id
}

var embySiteEpisodeMap struct {
	mu sync.Mutex
	m  map[string]embySiteEpisodeMapEntry // key: emitted episode Id
}

func embyBuildSiteSeasonID(seriesID string, seasonNo int, label string) string {
	sid := strings.TrimSpace(seriesID)
	if sid == "" {
		return ""
	}
	lb := strings.TrimSpace(label)
	if lb == "" {
		lb = "season"
	}
	return "sitesea_" + embyStableHex32("sitesea|"+sid+"|"+strconv.Itoa(seasonNo)+"|"+lb)
}

func embyBuildSiteEpisodeID(seasonID string, episodeIndex int, episodeURL string) string {
	sid := strings.TrimSpace(seasonID)
	if sid == "" {
		return ""
	}
	return "siteep_" + embyStableHex32("siteep|"+sid+"|"+strconv.Itoa(episodeIndex)+"|"+strings.TrimSpace(episodeURL))
}

func embySiteDetailCacheGet(seriesID string) (embySiteDetailCacheEntry, bool) {
	k := strings.TrimSpace(seriesID)
	if k == "" {
		return embySiteDetailCacheEntry{}, false
	}
	now := time.Now()

	embySiteDetailCache.mu.Lock()
	defer embySiteDetailCache.mu.Unlock()
	if embySiteDetailCache.m == nil {
		return embySiteDetailCacheEntry{}, false
	}
	e, ok := embySiteDetailCache.m[k]
	if !ok {
		return embySiteDetailCacheEntry{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embySiteDetailCache.m, k)
		return embySiteDetailCacheEntry{}, false
	}
	return e, true
}

func embySiteDetailCachePut(seriesID string, pans []catpawopen.Pan, ttl time.Duration) {
	k := strings.TrimSpace(seriesID)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	entry := embySiteDetailCacheEntry{Pans: pans, ExpireAt: time.Now().Add(ttl)}

	embySiteDetailCache.mu.Lock()
	defer embySiteDetailCache.mu.Unlock()
	if embySiteDetailCache.m == nil {
		embySiteDetailCache.m = map[string]embySiteDetailCacheEntry{}
	}
	embySiteDetailCache.m[k] = entry
}

func embySiteSeasonMapPut(id string, e embySiteSeasonMapEntry, ttl time.Duration) {
	k := strings.TrimSpace(id)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	e.ExpireAt = time.Now().Add(ttl)

	embySiteSeasonMap.mu.Lock()
	defer embySiteSeasonMap.mu.Unlock()
	if embySiteSeasonMap.m == nil {
		embySiteSeasonMap.m = map[string]embySiteSeasonMapEntry{}
	}
	embySiteSeasonMap.m[k] = e
}

func embySiteSeasonMapGet(id string) (embySiteSeasonMapEntry, bool) {
	k := strings.TrimSpace(id)
	if k == "" {
		return embySiteSeasonMapEntry{}, false
	}
	now := time.Now()

	embySiteSeasonMap.mu.Lock()
	defer embySiteSeasonMap.mu.Unlock()
	if embySiteSeasonMap.m == nil {
		return embySiteSeasonMapEntry{}, false
	}
	e, ok := embySiteSeasonMap.m[k]
	if !ok {
		return embySiteSeasonMapEntry{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embySiteSeasonMap.m, k)
		return embySiteSeasonMapEntry{}, false
	}
	return e, true
}

func embySiteEpisodeMapPut(id string, e embySiteEpisodeMapEntry, ttl time.Duration) {
	k := strings.TrimSpace(id)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	e.ExpireAt = time.Now().Add(ttl)

	embySiteEpisodeMap.mu.Lock()
	defer embySiteEpisodeMap.mu.Unlock()
	if embySiteEpisodeMap.m == nil {
		embySiteEpisodeMap.m = map[string]embySiteEpisodeMapEntry{}
	}
	embySiteEpisodeMap.m[k] = e
}

func embySiteEpisodeMapGet(id string) (embySiteEpisodeMapEntry, bool) {
	k := strings.TrimSpace(id)
	if k == "" {
		return embySiteEpisodeMapEntry{}, false
	}
	now := time.Now()

	embySiteEpisodeMap.mu.Lock()
	defer embySiteEpisodeMap.mu.Unlock()
	if embySiteEpisodeMap.m == nil {
		return embySiteEpisodeMapEntry{}, false
	}
	e, ok := embySiteEpisodeMap.m[k]
	if !ok {
		return embySiteEpisodeMapEntry{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embySiteEpisodeMap.m, k)
		return embySiteEpisodeMapEntry{}, false
	}
	return e, true
}
