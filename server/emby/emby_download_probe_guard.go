package emby

import (
	"sync"
	"time"
)

// A short-lived guard to suppress heavy "smart" resolution/probing when some clients
// are only checking download capability (e.g. requesting item details with CanDownload field).
//
// Keyed by user+device+TMDB ID to avoid cross-user interference.
var embyDownloadProbeGuard = struct {
	mu sync.Mutex
	m  map[string]time.Time
}{
	m: map[string]time.Time{},
}

const embyDownloadProbeTTL = 2 * time.Second

func embyDownloadProbeKey(userID string, deviceID string, tmdbID int) string {
	if tmdbID <= 0 {
		return ""
	}
	u := userID
	if u == "" {
		u = "-"
	}
	d := deviceID
	if d == "" {
		d = "-"
	}
	return u + "|" + d + "|" + intToStr(tmdbID)
}

func embyNoteDownloadProbe(userID string, deviceID string, tmdbID int) {
	if tmdbID <= 0 {
		return
	}
	k := embyDownloadProbeKey(userID, deviceID, tmdbID)
	if k == "" {
		return
	}
	now := time.Now()
	embyDownloadProbeGuard.mu.Lock()
	defer embyDownloadProbeGuard.mu.Unlock()
	embyDownloadProbeGuard.m[k] = now.Add(embyDownloadProbeTTL)
	// Opportunistic cleanup.
	if len(embyDownloadProbeGuard.m) > 4096 {
		for kk, exp := range embyDownloadProbeGuard.m {
			if exp.Before(now) {
				delete(embyDownloadProbeGuard.m, kk)
			}
		}
	}
}

func embyIsDownloadProbeActive(userID string, deviceID string, tmdbID int) bool {
	if tmdbID <= 0 {
		return false
	}
	k := embyDownloadProbeKey(userID, deviceID, tmdbID)
	if k == "" {
		return false
	}
	now := time.Now()
	embyDownloadProbeGuard.mu.Lock()
	defer embyDownloadProbeGuard.mu.Unlock()
	exp, ok := embyDownloadProbeGuard.m[k]
	if !ok {
		return false
	}
	if exp.Before(now) {
		delete(embyDownloadProbeGuard.m, k)
		return false
	}
	return true
}
