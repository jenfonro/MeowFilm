package routes

import (
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

type embySearchCacheEntry struct {
	ExpireAt time.Time
	TMDB     []embyTMDBSearchItem
	Sites    []embySiteSearchHit
}

var embySearchCache struct {
	mu sync.Mutex
	m  map[string]embySearchCacheEntry
}

func embyCanonicalIncludeItemTypes(includeItemTypes string) string {
	raw := strings.TrimSpace(includeItemTypes)
	if raw == "" {
		// Match our default behavior: Movie + Series.
		return "movie,series"
	}
	seen := map[string]bool{}
	out := make([]string, 0, 4)
	for _, p := range strings.Split(raw, ",") {
		t := strings.ToLower(strings.TrimSpace(p))
		if t == "" || seen[t] {
			continue
		}
		seen[t] = true
		if t == "tv" {
			t = "series"
		}
		out = append(out, t)
	}
	if len(out) == 0 {
		return "movie,series"
	}
	sort.Strings(out)
	return strings.Join(out, ",")
}

func embySearchCacheKey(u *embyUser, includeItemTypes string, searchTerm string) string {
	uid := ""
	if u != nil {
		uid = strings.TrimSpace(u.ID)
	}
	it := embyCanonicalIncludeItemTypes(includeItemTypes)
	st := strings.ToLower(strings.TrimSpace(embyCanonicalSearchTerm(searchTerm)))
	return fmt.Sprintf("u:%s|types:%s|q:%s", uid, it, st)
}

func embySearchCacheGet(key string) (embySearchCacheEntry, bool) {
	k := strings.TrimSpace(key)
	if k == "" {
		return embySearchCacheEntry{}, false
	}
	now := time.Now()
	embySearchCache.mu.Lock()
	defer embySearchCache.mu.Unlock()
	if embySearchCache.m == nil {
		return embySearchCacheEntry{}, false
	}
	e, ok := embySearchCache.m[k]
	if !ok {
		return embySearchCacheEntry{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embySearchCache.m, k)
		return embySearchCacheEntry{}, false
	}
	return e, true
}

func embySearchCachePut(key string, e embySearchCacheEntry, ttl time.Duration) {
	k := strings.TrimSpace(key)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	e.ExpireAt = time.Now().Add(ttl)
	embySearchCache.mu.Lock()
	defer embySearchCache.mu.Unlock()
	if embySearchCache.m == nil {
		embySearchCache.m = map[string]embySearchCacheEntry{}
	}
	embySearchCache.m[k] = e
}
