package emby_service

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/server/smart"
)

type playbackOfferStage string

const (
	playbackOfferStageManualList    playbackOfferStage = "manual_list"
	playbackOfferStageManualDetail  playbackOfferStage = "manual_detail"
	playbackOfferStageHistoryList   playbackOfferStage = "history_list"
	playbackOfferStageHistoryDetail playbackOfferStage = "history_detail"
	playbackOfferStageFull          playbackOfferStage = "full"
)

type playbackQueuedOffer struct {
	Offer     smart.PlaybackOffer
	Stage     playbackOfferStage
	ArrivedAt time.Time
}

type playbackControlEntry struct {
	UserID        int64
	DeviceID      string
	ItemID        string
	MediaSourceID string
	PlaySessionID string
	CacheKey      string

	Kind           string
	SubKind        string
	PreferSeasonNo int
	HasMultiSeason bool
	MatchSettings  smart.PlaybackSettings

	Started      bool
	Stopped      bool
	UpdatedAt    time.Time
	playbackDone bool

	offers      []playbackQueuedOffer
	offerSeen   map[string]struct{}
	triedFailed map[uint64]struct{}

	manualListDone    bool
	manualDetailDone  bool
	manualDone        bool
	historyListDone   bool
	historyDetailDone bool
	historyDone       bool
	fullDone          bool

	mu       sync.Mutex
	cond     *sync.Cond
	stopCh   chan struct{}
	stopOnce sync.Once
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

func ensurePlaybackControlEntry(userID int64, deviceID string, itemID string, mediaSourceID string, playSessionID string, cacheKey string, kind string, subKind string, preferSeasonNo int, hasMultiSeason bool, matchSettings smart.PlaybackSettings) *playbackControlEntry {
	sessionID := strings.TrimSpace(playSessionID)
	if sessionID == "" {
		return nil
	}
	playbackControl.mu.Lock()
	defer playbackControl.mu.Unlock()
	if existing, ok := playbackControl.bySession[sessionID]; ok && existing != nil {
		playbackControlApplyContext(existing, kind, subKind, preferSeasonNo, hasMultiSeason, matchSettings)
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
		offerSeen:     map[string]struct{}{},
		triedFailed:   map[uint64]struct{}{},
	}
	playbackControlApplyContext(entry, kind, subKind, preferSeasonNo, hasMultiSeason, matchSettings)
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

func playbackControlApplyContext(entry *playbackControlEntry, kind string, subKind string, preferSeasonNo int, hasMultiSeason bool, matchSettings smart.PlaybackSettings) {
	if entry == nil {
		return
	}
	if strings.TrimSpace(kind) != "" {
		entry.Kind = strings.TrimSpace(kind)
	}
	if strings.TrimSpace(subKind) != "" {
		entry.SubKind = strings.TrimSpace(subKind)
	}
	if preferSeasonNo > 0 {
		entry.PreferSeasonNo = preferSeasonNo
	}
	if hasMultiSeason {
		entry.HasMultiSeason = true
	}
	if len(matchSettings.OrderedRules) > 0 || len(matchSettings.KeywordTokensLower) > 0 || len(matchSettings.PanMatchEntries) > 0 {
		entry.MatchSettings = matchSettings
	}
}

func EnsurePlaybackControl(userID int64, deviceID string, itemID string, mediaSourceID string, playSessionID string, cacheKey string) <-chan struct{} {
	entry := ensurePlaybackControlEntry(userID, deviceID, itemID, mediaSourceID, playSessionID, cacheKey, "", "", 0, false, smart.PlaybackSettings{})
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
	return enqueuePlaybackOffer(playSessionID, mediaSourceID, cacheKey, playbackOfferStageHistoryList, offer)
}

func EnqueueManualListOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	return enqueuePlaybackOffer(playSessionID, mediaSourceID, cacheKey, playbackOfferStageManualList, offer)
}

func EnqueueManualDetailOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	return enqueuePlaybackOffer(playSessionID, mediaSourceID, cacheKey, playbackOfferStageManualDetail, offer)
}

func EnqueueHistoryDetailOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	return enqueuePlaybackOffer(playSessionID, mediaSourceID, cacheKey, playbackOfferStageHistoryDetail, offer)
}

func EnqueueFullOffer(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) bool {
	return enqueuePlaybackOffer(playSessionID, mediaSourceID, cacheKey, playbackOfferStageFull, offer)
}

func enqueuePlaybackOffer(playSessionID string, mediaSourceID string, cacheKey string, stage playbackOfferStage, offer smart.PlaybackOffer) bool {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return false
	}
	item := playbackQueuedOffer{
		Offer:     clonePlaybackOffer(offer),
		Stage:     stage,
		ArrivedAt: time.Now(),
	}
	key := playbackOfferCandidateKey(item.Offer)
	entry.mu.Lock()
	defer entry.mu.Unlock()
	if entry.playbackDone {
		return false
	}
	if key != "" {
		if _, ok := entry.offerSeen[key]; ok {
			return false
		}
		entry.offerSeen[key] = struct{}{}
	}
	entry.offers = append(entry.offers, item)
	entry.cond.Broadcast()
	return true
}

func CloseHistoryListOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.historyListDone = true
	entry.historyDone = entry.historyListDone && entry.historyDetailDone
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseManualListOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.manualListDone = true
	entry.manualDone = entry.manualListDone && entry.manualDetailDone
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseManualDetailOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.manualDetailDone = true
	entry.manualDone = entry.manualListDone && entry.manualDetailDone
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseHistoryDetailOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.historyDetailDone = true
	entry.historyDone = entry.historyListDone && entry.historyDetailDone
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func CloseFullOffers(playSessionID string, mediaSourceID string, cacheKey string) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	entry.mu.Lock()
	entry.fullDone = true
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
	entry.stopOnce.Do(func() { close(entry.stopCh) })
	entry.mu.Unlock()
	entry.cond.Broadcast()
}

func MarkPlaybackOfferFailed(playSessionID string, mediaSourceID string, cacheKey string, offer smart.PlaybackOffer) {
	entry := loadPlaybackControlEntry(playSessionID, mediaSourceID, cacheKey)
	if entry == nil {
		return
	}
	key := smart.PlaybackAttemptKey(offer.Cand)
	entry.mu.Lock()
	entry.triedFailed[key] = struct{}{}
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
	if !entry.manualDone {
		return "manual_only"
	}
	if !entry.historyDone {
		return "history_only"
	}
	if !entry.fullDone || len(entry.offers) > 0 {
		return "all_open"
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
		bestIdx := playbackPickBestOfferIndexLocked(entry)
		if bestIdx >= 0 {
			item := entry.offers[bestIdx]
			entry.offers = append(entry.offers[:bestIdx], entry.offers[bestIdx+1:]...)
			return clonePlaybackOffer(item.Offer), true
		}
		if entry.manualDone && entry.historyDone && entry.fullDone {
			return smart.PlaybackOffer{}, false
		}
		entry.cond.Wait()
	}
}

func playbackPickBestOfferIndexLocked(entry *playbackControlEntry) int {
	bestIdx := -1
	for idx, item := range entry.offers {
		if !playbackOfferVisible(entry, item) {
			continue
		}
		if _, failed := entry.triedFailed[smart.PlaybackAttemptKey(item.Offer.Cand)]; failed {
			continue
		}
		if bestIdx < 0 {
			bestIdx = idx
			continue
		}
		if playbackOfferBetter(entry, item, entry.offers[bestIdx]) {
			bestIdx = idx
		}
	}
	return bestIdx
}

func playbackOfferVisible(entry *playbackControlEntry, item playbackQueuedOffer) bool {
	if entry == nil {
		return false
	}
	if !entry.manualDone {
		return item.Stage == playbackOfferStageManualList || item.Stage == playbackOfferStageManualDetail
	}
	if !entry.historyDone {
		return item.Stage == playbackOfferStageHistoryList || item.Stage == playbackOfferStageHistoryDetail
	}
	return true
}

func playbackOfferBetter(entry *playbackControlEntry, a playbackQueuedOffer, b playbackQueuedOffer) bool {
	if smart.CompareSmartMatch(a.Offer.Cand, b.Offer.Cand, entry.HasMultiSeason, entry.PreferSeasonNo, entry.MatchSettings) < 0 {
		return true
	}
	if smart.CompareSmartMatch(a.Offer.Cand, b.Offer.Cand, entry.HasMultiSeason, entry.PreferSeasonNo, entry.MatchSettings) > 0 {
		return false
	}
	if a.ArrivedAt.Equal(b.ArrivedAt) {
		return false
	}
	return a.ArrivedAt.Before(b.ArrivedAt)
}

func playbackOfferCandidateKey(offer smart.PlaybackOffer) string {
	rawName := firstNonEmptyString(strings.TrimSpace(offer.Cand.RawName), strings.TrimSpace(smart.FirstRawNameFromURL(offer.Cand.Ep.URL)))
	return strings.TrimSpace(offer.Cand.SiteKey) + "|" + strings.TrimSpace(offer.Cand.SiteDetail) + "|" + strings.TrimSpace(offer.Cand.PanFlag) + "|" + rawName
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
