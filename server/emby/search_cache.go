package emby

import (
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

type SearchCacheEntry[TMDB any, Site any] struct {
	ExpireAt time.Time
	TMDB     []TMDB
	Sites    []Site
}

var searchCache struct {
	mu sync.Mutex
	m  map[string]any
}

func CanonicalIncludeItemTypes(includeItemTypes string) string {
	raw := strings.TrimSpace(includeItemTypes)
	if raw == "" {
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

func SearchCacheKey(userID string, includeItemTypes string, searchTerm string) string {
	uid := strings.TrimSpace(userID)
	it := CanonicalIncludeItemTypes(includeItemTypes)
	st := strings.ToLower(strings.TrimSpace(CanonicalSearchTerm(searchTerm)))
	return fmt.Sprintf("u:%s|types:%s|q:%s", uid, it, st)
}

func SearchCacheGet[TMDB any, Site any](key string) (SearchCacheEntry[TMDB, Site], bool) {
	k := strings.TrimSpace(key)
	if k == "" {
		return SearchCacheEntry[TMDB, Site]{}, false
	}
	now := time.Now()
	searchCache.mu.Lock()
	defer searchCache.mu.Unlock()
	if searchCache.m == nil {
		return SearchCacheEntry[TMDB, Site]{}, false
	}
	v, ok := searchCache.m[k]
	if !ok {
		return SearchCacheEntry[TMDB, Site]{}, false
	}
	e, ok := v.(SearchCacheEntry[TMDB, Site])
	if !ok {
		return SearchCacheEntry[TMDB, Site]{}, false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(searchCache.m, k)
		return SearchCacheEntry[TMDB, Site]{}, false
	}
	return e, true
}

func SearchCachePut[TMDB any, Site any](key string, e SearchCacheEntry[TMDB, Site], ttl time.Duration) {
	k := strings.TrimSpace(key)
	if k == "" {
		return
	}
	if ttl <= 0 {
		ttl = 30 * time.Minute
	}
	e.ExpireAt = time.Now().Add(ttl)
	searchCache.mu.Lock()
	defer searchCache.mu.Unlock()
	if searchCache.m == nil {
		searchCache.m = map[string]any{}
	}
	searchCache.m[k] = e
}
