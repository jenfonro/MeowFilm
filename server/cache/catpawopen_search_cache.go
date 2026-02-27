package cache

import (
	"encoding/json"
	"errors"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawopen"
)

type catPawOpenSearchRawValue struct {
	JSON []byte
}

var catPawOpenSearchCache = NewTwoTierTTLInflightCache[catPawOpenSearchRawValue](5*time.Minute, 4096, 1*time.Minute, 8192)

func catPawOpenSearchCacheKey(apiBase string, spiderAPI string, wd string, page int) string {
	q := strings.Join(strings.Fields(strings.TrimSpace(wd)), " ")
	return strings.TrimSpace(apiBase) + "|" + strings.TrimSpace(spiderAPI) + "|q:" + q + "|p:" + strconv.Itoa(page)
}

// RequestSpiderSearchCachedWithTimeout calls CatPawOpen spider "search" and caches the full raw JSON response.
// Cache key includes apiBase to avoid cross-user pollution when apiBase is user-dependent.
func RequestSpiderSearchCachedWithTimeout(apiBase string, spiderAPI string, wd string, page int, timeout time.Duration) (map[string]any, error) {
	base := strings.TrimSpace(apiBase)
	sp := strings.TrimSpace(spiderAPI)
	q := strings.TrimSpace(wd)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	if sp == "" || q == "" {
		return nil, errors.New("invalid args")
	}
	if page <= 0 {
		page = 1
	}

	key := catPawOpenSearchCacheKey(base, sp, q, page)
	val, _, err := catPawOpenSearchCache.Do(key, func() (catPawOpenSearchRawValue, error) {
		raw, e := catpawopen.RequestSpiderWithTimeout(base, sp, "search", map[string]any{"wd": q, "page": page}, timeout)
		if e != nil {
			return catPawOpenSearchRawValue{}, e
		}
		b, e2 := json.Marshal(raw)
		if e2 != nil {
			return catPawOpenSearchRawValue{}, e2
		}
		return catPawOpenSearchRawValue{JSON: b}, nil
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

// RequestSpiderSearchCached uses the default CatPawOpen client timeout.
func RequestSpiderSearchCached(apiBase string, spiderAPI string, wd string, page int) (map[string]any, error) {
	return RequestSpiderSearchCachedWithTimeout(apiBase, spiderAPI, wd, page, 12*time.Second)
}

