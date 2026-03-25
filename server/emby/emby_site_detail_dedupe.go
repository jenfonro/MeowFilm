package emby

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

type embySiteDetailDedupEntry struct {
	Pans     []catpawrunner.Pan
	ErrMsg   string
	ExpireAt time.Time
}

var embySiteDetailDedup struct {
	mu       sync.Mutex
	cache    map[string]embySiteDetailDedupEntry
	inFlight map[string]chan struct{}
}

const embySiteDetailDedupTTL = 12 * time.Second

func embyFetchSiteDetailPansDedup(database *db.DB, u *embyUser, spiderAPI string, siteDetail string) ([]catpawrunner.Pan, error) {
	if database == nil {
		return nil, nil
	}
	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	// apiBase is user-dependent, so include it in the key to avoid cross-user pollution.
	key := strings.TrimSpace(apiBase) + "|" + strings.TrimSpace(spiderAPI) + "|" + strings.TrimSpace(siteDetail)
	if key == "||" {
		return nil, nil
	}
	now := time.Now()

	embySiteDetailDedup.mu.Lock()
	if embySiteDetailDedup.cache == nil {
		embySiteDetailDedup.cache = map[string]embySiteDetailDedupEntry{}
	}
	if embySiteDetailDedup.inFlight == nil {
		embySiteDetailDedup.inFlight = map[string]chan struct{}{}
	}
	// quick cleanup to avoid unbounded growth
	if len(embySiteDetailDedup.cache) > 2048 {
		for k, v := range embySiteDetailDedup.cache {
			if !v.ExpireAt.IsZero() && now.After(v.ExpireAt) {
				delete(embySiteDetailDedup.cache, k)
			}
		}
	}

	if hit, ok := embySiteDetailDedup.cache[key]; ok && !hit.ExpireAt.IsZero() && now.Before(hit.ExpireAt) {
		pans := hit.Pans
		errMsg := strings.TrimSpace(hit.ErrMsg)
		embySiteDetailDedup.mu.Unlock()
		if errMsg != "" {
			return pans, errorString(errMsg)
		}
		return pans, nil
	}
	if ch, ok := embySiteDetailDedup.inFlight[key]; ok && ch != nil {
		embySiteDetailDedup.mu.Unlock()
		<-ch
		// After wait, try cache once.
		embySiteDetailDedup.mu.Lock()
		hit, ok := embySiteDetailDedup.cache[key]
		embySiteDetailDedup.mu.Unlock()
		if ok && !hit.ExpireAt.IsZero() && time.Now().Before(hit.ExpireAt) {
			errMsg := strings.TrimSpace(hit.ErrMsg)
			if errMsg != "" {
				return hit.Pans, errorString(errMsg)
			}
			return hit.Pans, nil
		}
		// Fall through to fetch again.
		embySiteDetailDedup.mu.Lock()
	}

	ch := make(chan struct{})
	embySiteDetailDedup.inFlight[key] = ch
	embySiteDetailDedup.mu.Unlock()

	var (
		pans []catpawrunner.Pan
		err  error
	)
	defer func() {
		embySiteDetailDedup.mu.Lock()
		delete(embySiteDetailDedup.inFlight, key)
		embySiteDetailDedup.cache[key] = embySiteDetailDedupEntry{
			Pans:     pans,
			ErrMsg:   errString(err),
			ExpireAt: time.Now().Add(embySiteDetailDedupTTL),
		}
		embySiteDetailDedup.mu.Unlock()
		close(ch)
	}()

	pans, err = embyFetchSiteDetailPans(database, u, spiderAPI, siteDetail)
	return pans, err
}

func errString(err error) string {
	if err == nil {
		return ""
	}
	return strings.TrimSpace(err.Error())
}

// errorString lets us convert a cached string back to error without importing fmt.
type errorString string

func (e errorString) Error() string { return string(e) }
