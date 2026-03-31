package emby_service

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/server/smart"
)

type playbackOfferStage string

const (
	playbackOfferStageHistoryList   playbackOfferStage = "history_list"
	playbackOfferStageHistoryDetail playbackOfferStage = "history_detail"
	playbackOfferStageFull          playbackOfferStage = "full"
)

type playbackQueuedOffer struct {
	Offer smart.PlaybackOffer
	Stage playbackOfferStage
	Tier  int
}

type playbackControlEntry struct {
	UserID              int64
	DeviceID            string
	ItemID              string
	MediaSourceID       string
	PlaySessionID       string
	CacheKey            string
	Started             bool
	Stopped             bool
	UpdatedAt           time.Time
	mu                  sync.Mutex
	cond                *sync.Cond
	historyListQ        []playbackQueuedOffer
	historyDetailQ      []playbackQueuedOffer
	fullTier1Q          []playbackQueuedOffer
	fullTier2Q          []playbackQueuedOffer
	fullTier3Q          []playbackQueuedOffer
	historyListClosed   bool
	historyDetailClosed bool
	fullClosed          bool
	playbackDone        bool
	stopCh              chan struct{}
	stopOnce            sync.Once
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
		stopCh:        make(chan struct{}),
	}
	entry.cond = sync.NewCond(&entry.mu)
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

func RebindPlaybackControlIdentity(playSessionID string, mediaSourceID string, cacheKey string, newPlaySessionID string, newMediaSourceID string) {
	id := strings.TrimSpace(playSessionID)
	ms := strings.TrimSpace(mediaSourceID)
	ck := strings.TrimSpace(cacheKey)
	newID := strings.TrimSpace(newPlaySessionID)
	newMS := strings.TrimSpace(newMediaSourceID)

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
	if !ok || entry == nil {
		return
	}
	if newID == "" {
		newID = entry.PlaySessionID
	}
	if newMS == "" {
		newMS = entry.MediaSourceID
	}
	oldID := entry.PlaySessionID
	oldMS := entry.MediaSourceID
	if oldMS != "" {
		delete(playbackControl.byMedia, oldMS)
	}
	if oldID != "" && oldID != newID {
		delete(playbackControl.bySession, oldID)
	}
	entry.PlaySessionID = newID
	entry.MediaSourceID = newMS
	entry.UpdatedAt = time.Now()
	playbackControl.bySession[newID] = entry
	if newMS != "" {
		playbackControl.byMedia[newMS] = newID
	}
	if entry.CacheKey != "" {
		playbackControl.byCache[entry.CacheKey] = newID
	}
	if ck != "" {
		playbackControl.byCache[ck] = newID
	}
}

func EnqueueHistoryListOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return false
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	if entry.playbackDone || entry.historyListClosed {
		return false
	}
	entry.historyListQ = append(entry.historyListQ, playbackQueuedOffer{
		Offer: clonePlaybackOffer(offer),
		Stage: playbackOfferStageHistoryList,
	})
	entry.cond.Broadcast()
	return true
}

func EnqueueHistoryDetailOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return false
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	if entry.playbackDone || entry.historyDetailClosed {
		return false
	}
	entry.historyDetailQ = append(entry.historyDetailQ, playbackQueuedOffer{
		Offer: clonePlaybackOffer(offer),
		Stage: playbackOfferStageHistoryDetail,
	})
	entry.cond.Broadcast()
	return true
}

func EnqueueFullOffer(playSessionID string, mediaSourceID string, cacheKey string, tier int, offer smart.PlaybackOffer) bool {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return false
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	if entry.playbackDone || entry.fullClosed {
		return false
	}
	item := playbackQueuedOffer{
		Offer: clonePlaybackOffer(offer),
		Stage: playbackOfferStageFull,
		Tier:  tier,
	}
	switch tier {
	case 1:
		entry.fullTier1Q = append(entry.fullTier1Q, item)
	case 2:
		entry.fullTier2Q = append(entry.fullTier2Q, item)
	default:
		entry.fullTier3Q = append(entry.fullTier3Q, item)
	}
	entry.cond.Broadcast()
	return true
}

func CloseHistoryListOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.historyListClosed = true
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseHistoryDetailOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.historyDetailClosed = true
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseFullOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.fullClosed = true
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func MarkPlaybackDone(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.playbackDone = true
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CurrentPlaybackOfferStage(playSessionID string, mediaSourceID string, cacheKey string) string {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return ""
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	if !entry.historyListClosed {
		return string(playbackOfferStageHistoryList)
	}
	if !entry.historyDetailClosed {
		return string(playbackOfferStageHistoryDetail)
	}
	if !entry.fullClosed || len(entry.fullTier1Q) > 0 || len(entry.fullTier2Q) > 0 || len(entry.fullTier3Q) > 0 {
		return string(playbackOfferStageFull)
	}
	return ""
}

func NextPlaybackOffer(playSessionID string, mediaSourceID string, cacheKey string) (smart.PlaybackOffer, bool) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return smart.PlaybackOffer{}, false
	}
	entry.mu.Lock()
	defer entry.mu.Unlock()
	for {
		if entry.playbackDone {
			return smart.PlaybackOffer{}, false
		}
		if len(entry.historyListQ) > 0 {
			item := entry.historyListQ[0]
			entry.historyListQ = entry.historyListQ[1:]
			return clonePlaybackOffer(item.Offer), true
		}
		if !entry.historyListClosed {
			entry.cond.Wait()
			continue
		}
		if len(entry.historyDetailQ) > 0 {
			item := entry.historyDetailQ[0]
			entry.historyDetailQ = entry.historyDetailQ[1:]
			return clonePlaybackOffer(item.Offer), true
		}
		if !entry.historyDetailClosed {
			entry.cond.Wait()
			continue
		}
		if len(entry.fullTier1Q) > 0 {
			item := entry.fullTier1Q[0]
			entry.fullTier1Q = entry.fullTier1Q[1:]
			return clonePlaybackOffer(item.Offer), true
		}
		if len(entry.fullTier2Q) > 0 {
			item := entry.fullTier2Q[0]
			entry.fullTier2Q = entry.fullTier2Q[1:]
			return clonePlaybackOffer(item.Offer), true
		}
		if len(entry.fullTier3Q) > 0 {
			item := entry.fullTier3Q[0]
			entry.fullTier3Q = entry.fullTier3Q[1:]
			return clonePlaybackOffer(item.Offer), true
		}
		if entry.fullClosed {
			return smart.PlaybackOffer{}, false
		}
		entry.cond.Wait()
	}
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
