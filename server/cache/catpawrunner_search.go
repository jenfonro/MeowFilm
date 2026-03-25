package cache

import (
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

// RequestSpiderSearchWithTimeout calls catpawrunner spider "search" directly.
func RequestSpiderSearchWithTimeout(apiBase string, spiderAPI string, wd string, page int, timeout time.Duration) (map[string]any, error) {
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
	raw, err := catpawrunner.RequestSpiderWithTimeout(base, sp, "search", map[string]any{"wd": q, "page": page}, timeout)
	if err != nil {
		return nil, err
	}
	if raw == nil {
		return map[string]any{}, nil
	}
	return raw, nil
}

// RequestSpiderSearchDirect uses the default catpawrunner client timeout.
func RequestSpiderSearchDirect(apiBase string, spiderAPI string, wd string, page int) (map[string]any, error) {
	return RequestSpiderSearchWithTimeout(apiBase, spiderAPI, wd, page, 12*time.Second)
}
