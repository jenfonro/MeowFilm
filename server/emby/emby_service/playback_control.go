package emby_service

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/server/smart"
)

type playbackControlEntry struct {
	UserID        int64
	DeviceID      string
	ItemID        string
	MediaSourceID string
	PlaySessionID string
	CacheKey      string
	Started       bool
	Stopped       bool
	UpdatedAt     time.Time
	offersCh      chan smart.PlaybackOffer
	offersOnce    sync.Once
	stopCh        chan struct{}
	stopOnce      sync.Once
}

var playbackControl = struct {
	mu        sync.Mutex
	bySession map[string]*playbackControlEntry
	byMedia   map[string]string
	byCache   map[string]string
}{
	bySession: map[string]*playbackControlEntry{},
	byMedia:   map[string]string{},
	byCache:   map[string]string{},
}

func ensurePlaybackControlEntry(userID int64, deviceID string, itemID string, mediaSourceID string, playSessionID string, cacheKey string) *playbackControlEntry {
	sessionID := strings.TrimSpace(playSessionID)
	if sessionID == "" {
		return nil
	}
	playbackControl.mu.Lock()
	defer playbackControl.mu.Unlock()
	if existing, ok := playbackControl.bySession[sessionID]; ok && existing != nil {
		return existing
	}
	entry := &playbackControlEntry{
		UserID:        userID,
		DeviceID:      strings.TrimSpace(deviceID),
		ItemID:        strings.TrimSpace(itemID),
		MediaSourceID: strings.TrimSpace(mediaSourceID),
		PlaySessionID: sessionID,
		CacheKey:      strings.TrimSpace(cacheKey),
		UpdatedAt:     time.Now(),
		offersCh:      make(chan smart.PlaybackOffer, 32),
		stopCh:        make(chan struct{}),
	}
	playbackControl.bySession[sessionID] = entry
	if entry.MediaSourceID != "" {
		playbackControl.byMedia[entry.MediaSourceID] = sessionID
	}
	if entry.CacheKey != "" {
		playbackControl.byCache[entry.CacheKey] = sessionID
	}
	return entry
}

func EnsurePlaybackControl(userID int64, deviceID string, itemID string, mediaSourceID string, playSessionID string, cacheKey string) <-chan struct{} {
	entry := ensurePlaybackControlEntry(userID, deviceID, itemID, mediaSourceID, playSessionID, cacheKey)
	if entry == nil {
		return nil
	}
	return entry.stopCh
}

func MarkPlaybackSessionStarted(playSessionID string, mediaSourceID string, cacheKey string) {
	updatePlaybackControl(playSessionID, mediaSourceID, cacheKey, func(entry *playbackControlEntry) {
		entry.Started = true
		entry.Stopped = false
		entry.stopOnce.Do(func() { close(entry.stopCh) })
	})
}

func MarkPlaybackSessionStopped(playSessionID string, mediaSourceID string, cacheKey string) {
	updatePlaybackControl(playSessionID, mediaSourceID, cacheKey, func(entry *playbackControlEntry) {
		entry.Stopped = true
		entry.UpdatedAt = time.Now()
		entry.stopOnce.Do(func() { close(entry.stopCh) })
	})
}

func updatePlaybackControl(playSessionID string, mediaSourceID string, cacheKey string, fn func(entry *playbackControlEntry)) {
	id := strings.TrimSpace(playSessionID)
	ms := strings.TrimSpace(mediaSourceID)
	ck := strings.TrimSpace(cacheKey)
	playbackControl.mu.Lock()
	defer playbackControl.mu.Unlock()
	if id == "" && ms != "" {
		id = playbackControl.byMedia[ms]
	}
	if id == "" && ck != "" {
		id = playbackControl.byCache[ck]
	}
	if id == "" {
		return
	}
	entry, ok := playbackControl.bySession[id]
	if !ok {
		return
	}
	fn(entry)
	entry.UpdatedAt = time.Now()
}

func RegisterPlaybackControl(target PlaybackStreamTarget, cacheKey string) <-chan struct{} {
	return EnsurePlaybackControl(
		target.UserID,
		target.DeviceID,
		target.ItemID,
		target.MediaSourceID,
		target.PlaySessionID,
		cacheKey,
	)
}

func EnqueuePlaybackOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return false
	}
	entry.offersCh <- clonePlaybackOffer(offer)
	return true
}

func ClosePlaybackOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.offersOnce.Do(func() { close(entry.offersCh) })
}

func LoadPlaybackOffers(playSessionID string, mediaSourceID string, cacheKey string) (<-chan smart.PlaybackOffer, bool) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return nil, false
	}
	return entry.offersCh, true
}

func IsPlaybackResolveStopped(stopCh <-chan struct{}) bool {
	if stopCh == nil {
		return false
	}
	select {
	case <-stopCh:
		return true
	default:
		return false
	}
}

func loadPlaybackControlEntry(playSessionID string, mediaSourceID string, cacheKey string) *playbackControlEntry {
	id := strings.TrimSpace(playSessionID)
	ms := strings.TrimSpace(mediaSourceID)
	ck := strings.TrimSpace(cacheKey)
	playbackControl.mu.Lock()
	defer playbackControl.mu.Unlock()
	if id == "" && ms != "" {
		id = playbackControl.byMedia[ms]
	}
	if id == "" && ck != "" {
		id = playbackControl.byCache[ck]
	}
	if id == "" {
		return nil
	}
	return playbackControl.bySession[id]
}
