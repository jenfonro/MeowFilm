package netdisk

import (
	"log"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const panPlayActiveTTL = 30 * time.Minute

type panPlayActivityStore struct {
	mu          sync.Mutex
	activeUntil map[string]time.Time
}

var panPlayActivity = panPlayActivityStore{
	activeUntil: map[string]time.Time{},
}

func markPanPlayActivity(provider string, now time.Time, ttl time.Duration) {
	key := strings.TrimSpace(provider)
	if key == "" {
		return
	}
	if now.IsZero() {
		now = time.Now()
	}
	if ttl <= 0 {
		ttl = panPlayActiveTTL
	}
	panPlayActivity.mu.Lock()
	panPlayActivity.activeUntil[key] = now.Add(ttl)
	panPlayActivity.mu.Unlock()
}

func hasPanActivePlayback(provider string, now time.Time) bool {
	key := strings.TrimSpace(provider)
	if key == "" {
		return false
	}
	if now.IsZero() {
		now = time.Now()
	}
	panPlayActivity.mu.Lock()
	defer panPlayActivity.mu.Unlock()
	exp, ok := panPlayActivity.activeUntil[key]
	if !ok {
		return false
	}
	return now.Before(exp)
}

func NextDailyCleanupTime(now time.Time, loc *time.Location, hour int, minute int) time.Time {
	if loc == nil {
		loc = time.Local
	}
	base := now.In(loc)
	next := time.Date(base.Year(), base.Month(), base.Day(), hour, minute, 0, 0, loc)
	if !next.After(base) {
		next = next.Add(24 * time.Hour)
	}
	return next
}

func TryQuarkDailyCleanup(database *db.DB, now time.Time) (done bool, err error) {
	if database == nil {
		return true, nil
	}
	if hasPanActivePlayback("quark", now) {
		log.Printf("quark daily cleanup delayed: active playback")
		return false, nil
	}
	store := readPanLoginSettings(database)
	cookie := strings.TrimSpace(getPanField(store, "quark", "cookie"))
	if cookie == "" {
		log.Printf("quark daily cleanup skipped: missing cookie")
		return true, nil
	}
	rootFid, found, err := quarkFindFolderFid(panRootFolderName, cookie, "0")
	if err != nil {
		log.Printf("quark daily cleanup failed: find root err=%v", err)
		return false, err
	}
	if !found || strings.TrimSpace(rootFid) == "" {
		log.Printf("quark daily cleanup skipped: root not found")
		return true, nil
	}
	if err := quarkClearDir(rootFid, cookie); err != nil {
		log.Printf("quark daily cleanup failed: %v", err)
		return false, err
	}
	log.Printf("quark daily cleanup success: root=%s", panRootFolderName)
	return true, nil
}

func TryUCDailyCleanup(database *db.DB, now time.Time) (done bool, err error) {
	if database == nil {
		return true, nil
	}
	if hasPanActivePlayback("uc", now) {
		log.Printf("uc daily cleanup delayed: active playback")
		return false, nil
	}
	store := readPanLoginSettings(database)
	cookie := strings.TrimSpace(getPanField(store, "uc", "cookie"))
	if cookie == "" {
		log.Printf("uc daily cleanup skipped: missing cookie")
		return true, nil
	}
	rootFid, found, err := ucFindFolderFid(panRootFolderName, &cookie, "0")
	if err != nil {
		log.Printf("uc daily cleanup failed: find root err=%v", err)
		return false, err
	}
	if !found || strings.TrimSpace(rootFid) == "" {
		log.Printf("uc daily cleanup skipped: root not found")
		return true, nil
	}
	if err := ucClearDir(rootFid, &cookie); err != nil {
		log.Printf("uc daily cleanup failed: %v", err)
		return false, err
	}
	log.Printf("uc daily cleanup success: root=%s", panRootFolderName)
	return true, nil
}
