package cache

import (
	"encoding/json"
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawopen"
)

type catPawOpenDetailRawValue struct {
	JSON []byte
}

// Default TTLs for suppressing smart-playback storms:
// - successful detail responses are cached for 5 minutes
// - failures are cached for 1 minute (negative cache)
var catPawOpenDetailCache = NewTwoTierTTLInflightCache[catPawOpenDetailRawValue](5*time.Minute, 2048, 1*time.Minute, 4096)

func catPawOpenDetailCacheKey(apiBase string, spiderAPI string, videoID string) string {
	return strings.TrimSpace(apiBase) + "|" + strings.TrimSpace(spiderAPI) + "|" + strings.TrimSpace(videoID)
}

// RequestSpiderDetailCachedWithTimeout fetches CatPawOpen spider detail and caches the full raw JSON response.
// Cache key includes apiBase to avoid cross-user pollution when apiBase is user-dependent.
func RequestSpiderDetailCachedWithTimeout(apiBase string, spiderAPI string, videoID string, timeout time.Duration) (map[string]any, error) {
	base := strings.TrimSpace(apiBase)
	sp := strings.TrimSpace(spiderAPI)
	vid := strings.TrimSpace(videoID)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	if sp == "" || vid == "" {
		return nil, errors.New("invalid args")
	}

	key := catPawOpenDetailCacheKey(base, sp, vid)
	val, _, err := catPawOpenDetailCache.Do(key, func() (catPawOpenDetailRawValue, error) {
		raw, e := catpawopen.RequestSpiderWithTimeout(base, sp, "detail", map[string]any{"id": vid}, timeout)
		if e != nil {
			return catPawOpenDetailRawValue{}, e
		}
		b, e2 := json.Marshal(raw)
		if e2 != nil {
			return catPawOpenDetailRawValue{}, e2
		}
		return catPawOpenDetailRawValue{JSON: b}, nil
	})
	if err != nil {
		return nil, err
	}
	if len(val.JSON) == 0 {
		return map[string]any{}, nil
	}
	var out map[string]any
	if e := json.Unmarshal(val.JSON, &out); e != nil {
		return nil, e
	}
	if out == nil {
		out = map[string]any{}
	}
	return out, nil
}

// RequestSpiderDetailCached fetches CatPawOpen spider detail and caches the full raw JSON response.
// Cache key includes apiBase to avoid cross-user pollution when apiBase is user-dependent.
func RequestSpiderDetailCached(apiBase string, spiderAPI string, videoID string) (map[string]any, error) {
	return RequestSpiderDetailCachedWithTimeout(apiBase, spiderAPI, videoID, 12*time.Second)
}
