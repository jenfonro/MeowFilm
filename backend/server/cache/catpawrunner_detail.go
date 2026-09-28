package cache

import (
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

// RequestSpiderDetailWithTimeout fetches catpawrunner spider detail directly.
// MeowFilm no longer caches spider detail raw responses; this should be cached in catpawrunner itself.
func RequestSpiderDetailWithTimeout(apiBase string, spiderAPI string, siteDetail string, timeout time.Duration) (map[string]any, error) {
	base := strings.TrimSpace(apiBase)
	sp := strings.TrimSpace(spiderAPI)
	detail := strings.TrimSpace(siteDetail)
	if base == "" {
		return nil, errors.New("catpawrunner 接口地址未设置")
	}
	if sp == "" || detail == "" {
		return nil, errors.New("invalid args")
	}
	raw, err := catpawrunner.RequestSpiderWithTimeout(base, sp, "detail", map[string]any{"id": detail}, timeout)
	if err != nil {
		return nil, err
	}
	if raw == nil {
		return map[string]any{}, nil
	}
	return raw, nil
}

// RequestSpiderDetailDirect fetches catpawrunner spider detail directly.
func RequestSpiderDetailDirect(apiBase string, spiderAPI string, siteDetail string) (map[string]any, error) {
	return RequestSpiderDetailWithTimeout(apiBase, spiderAPI, siteDetail, 12*time.Second)
}
