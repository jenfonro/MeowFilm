package cache

import (
	"encoding/json"
	"errors"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

type catpawrunnerSearchRawValue struct {
	JSON []byte
}

var catpawrunnerSearchCache = NewTwoTierTTLInflightCache[catpawrunnerSearchRawValue](5*time.Minute, 4096, 1*time.Minute, 8192)

func catpawrunnerSearchCacheKey(apiBase string, spiderAPI string, wd string, page int) string {
	q := strings.Join(strings.Fields(strings.TrimSpace(wd)), " ")
	return strings.TrimSpace(apiBase) + "|" + strings.TrimSpace(spiderAPI) + "|q:" + q + "|p:" + strconv.Itoa(page)
}

// RequestSpiderSearchCachedWithTimeout calls catpawrunner spider "search" and caches the full raw JSON response.
// Cache key includes apiBase to avoid cross-user pollution when apiBase is user-dependent.
func RequestSpiderSearchCachedWithTimeout(apiBase string, spiderAPI string, wd string, page int, timeout time.Duration) (map[string]any, error) {
	base := strings.TrimSpace(apiBase)
	sp := strings.TrimSpace(spiderAPI)
	q := strings.TrimSpace(wd)
	if base == "" {
		return nil, errors.New("catpawrunner 接口地址未设置")
	}
	if sp == "" || q == "" {
		return nil, errors.New("invalid args")
	}
	if page <= 0 {
		page = 1
	}

	key := catpawrunnerSearchCacheKey(base, sp, q, page)
	val, _, err := catpawrunnerSearchCache.Do(key, func() (catpawrunnerSearchRawValue, error) {
		raw, e := catpawrunner.RequestSpiderWithTimeout(base, sp, "search", map[string]any{"wd": q, "page": page}, timeout)
		if e != nil {
			return catpawrunnerSearchRawValue{}, e
		}
		b, e2 := json.Marshal(raw)
		if e2 != nil {
			return catpawrunnerSearchRawValue{}, e2
		}
		return catpawrunnerSearchRawValue{JSON: b}, nil
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

// RequestSpiderSearchCached uses the default catpawrunner client timeout.
func RequestSpiderSearchCached(apiBase string, spiderAPI string, wd string, page int) (map[string]any, error) {
	return RequestSpiderSearchCachedWithTimeout(apiBase, spiderAPI, wd, page, 12*time.Second)
}
