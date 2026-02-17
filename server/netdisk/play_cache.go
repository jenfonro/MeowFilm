package netdisk

import (
	"strings"
	"sync"
	"time"
)

const playCacheTTL = 3 * time.Minute

type playCacheEntry struct {
	url    string
	header map[string]string
	expAt  time.Time
}

type playCacheStore struct {
	mu sync.Mutex
	m  map[string]playCacheEntry
}

var playCache = &playCacheStore{m: map[string]playCacheEntry{}}

func normalizePlayCacheSeg(v string) string {
	return strings.TrimSpace(v)
}

func buildPlayCacheKey(provider string, parts ...string) string {
	p := strings.ToLower(normalizePlayCacheSeg(provider))
	segs := make([]string, 0, 1+len(parts))
	segs = append(segs, p)
	for _, raw := range parts {
		segs = append(segs, normalizePlayCacheSeg(raw))
	}
	return strings.Join(segs, "\n")
}

func copyStringMap(in map[string]string) map[string]string {
	if len(in) == 0 {
		return nil
	}
	out := make(map[string]string, len(in))
	for k, v := range in {
		kk := strings.TrimSpace(k)
		if kk == "" {
			continue
		}
		out[kk] = v
	}
	return out
}

func getPlayCache(key string) (url string, header map[string]string, ok bool) {
	k := normalizePlayCacheSeg(key)
	if k == "" {
		return "", nil, false
	}
	now := time.Now()
	playCache.mu.Lock()
	defer playCache.mu.Unlock()
	ent, exists := playCache.m[k]
	if !exists {
		return "", nil, false
	}
	if !ent.expAt.IsZero() && now.After(ent.expAt) {
		delete(playCache.m, k)
		return "", nil, false
	}
	return ent.url, copyStringMap(ent.header), strings.TrimSpace(ent.url) != ""
}

func setPlayCache(key string, url string, header map[string]string) {
	k := normalizePlayCacheSeg(key)
	u := normalizePlayCacheSeg(url)
	if k == "" || u == "" {
		return
	}
	playCache.mu.Lock()
	playCache.m[k] = playCacheEntry{
		url:    u,
		header: copyStringMap(header),
		expAt:  time.Now().Add(playCacheTTL),
	}
	playCache.mu.Unlock()
}

