package emby_service

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/server/smart"
)

type playbackCacheEntry struct {
	Target   PlaybackStreamTarget
	ExpireAt time.Time
}

type playbackCacheStore struct {
	mu        sync.Mutex
	byMedia   map[string]playbackCacheEntry
	bySession map[string]string
	byReplay  map[string]string
}

func newPlaybackCacheStore() *playbackCacheStore {
	return &playbackCacheStore{
		byMedia:   map[string]playbackCacheEntry{},
		bySession: map[string]string{},
		byReplay:  map[string]string{},
	}
}

func (s *playbackCacheStore) cleanupLocked(now time.Time) {
	if s == nil {
		return
	}
	for mediaSourceID, entry := range s.byMedia {
		if entry.ExpireAt.Before(now) {
			delete(s.byMedia, mediaSourceID)
		}
	}
	for sessionKey, mediaSourceID := range s.bySession {
		entry, ok := s.byMedia[mediaSourceID]
		if !ok || entry.ExpireAt.Before(now) {
			delete(s.bySession, sessionKey)
		}
	}
	for replayKey, mediaSourceID := range s.byReplay {
		entry, ok := s.byMedia[mediaSourceID]
		if !ok || entry.ExpireAt.Before(now) {
			delete(s.byReplay, replayKey)
		}
	}
}

func (s *playbackCacheStore) Set(target PlaybackStreamTarget, ttl time.Duration) {
	s.SetWithReplayKey(target, "", ttl)
}

func (s *playbackCacheStore) SetWithReplayKey(target PlaybackStreamTarget, replayKey string, ttl time.Duration) {
	if s == nil {
		return
	}
	if strings.TrimSpace(target.MediaSourceID) == "" {
		return
	}
	if strings.TrimSpace(target.FinalURL) == "" && strings.TrimSpace(target.Path) == "" && len(target.Offers) == 0 {
		return
	}
	now := time.Now()
	target.ExpireAt = now.Add(ttl)
	entry := playbackCacheEntry{
		Target:   clonePlaybackTarget(target),
		ExpireAt: target.ExpireAt,
	}
	sessionKey := playbackSessionKey(target.ItemID, target.MediaSourceID, target.PlaySessionID)
	s.mu.Lock()
	s.byMedia[target.MediaSourceID] = entry
	if sessionKey != "" {
		s.bySession[sessionKey] = target.MediaSourceID
	}
	if rk := strings.TrimSpace(replayKey); rk != "" {
		s.byReplay[rk] = target.MediaSourceID
	}
	s.cleanupLocked(now)
	s.mu.Unlock()
}

func (s *playbackCacheStore) GetByMediaSourceID(mediaSourceID string) (PlaybackStreamTarget, bool) {
	if s == nil {
		return PlaybackStreamTarget{}, false
	}
	key := strings.TrimSpace(mediaSourceID)
	if key == "" {
		return PlaybackStreamTarget{}, false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	entry, ok := s.byMedia[key]
	s.cleanupLocked(now)
	if !ok || entry.ExpireAt.Before(now) {
		return PlaybackStreamTarget{}, false
	}
	return clonePlaybackTarget(entry.Target), true
}

func (s *playbackCacheStore) GetBySession(itemID string, mediaSourceID string, playSessionID string) (PlaybackStreamTarget, bool) {
	if s == nil {
		return PlaybackStreamTarget{}, false
	}
	sessionKey := playbackSessionKey(itemID, mediaSourceID, playSessionID)
	if sessionKey == "" {
		return PlaybackStreamTarget{}, false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	mediaKey, ok := s.bySession[sessionKey]
	s.cleanupLocked(now)
	if !ok {
		return PlaybackStreamTarget{}, false
	}
	entry, ok := s.byMedia[mediaKey]
	if !ok || entry.ExpireAt.Before(now) {
		return PlaybackStreamTarget{}, false
	}
	return clonePlaybackTarget(entry.Target), true
}

func (s *playbackCacheStore) GetByReplayKey(replayKey string) (PlaybackStreamTarget, bool) {
	if s == nil {
		return PlaybackStreamTarget{}, false
	}
	key := strings.TrimSpace(replayKey)
	if key == "" {
		return PlaybackStreamTarget{}, false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	mediaKey, ok := s.byReplay[key]
	s.cleanupLocked(now)
	if !ok {
		return PlaybackStreamTarget{}, false
	}
	entry, ok := s.byMedia[mediaKey]
	if !ok || entry.ExpireAt.Before(now) {
		return PlaybackStreamTarget{}, false
	}
	target := clonePlaybackTarget(entry.Target)
	if strings.TrimSpace(target.FinalURL) == "" || strings.TrimSpace(target.MediaSourceID) == "" || strings.TrimSpace(target.PlaySessionID) == "" {
		return PlaybackStreamTarget{}, false
	}
	return target, true
}

func (s *playbackCacheStore) ExtendIfLow(mediaSourceID string, add time.Duration, capRemaining time.Duration) bool {
	if s == nil {
		return false
	}
	key := strings.TrimSpace(mediaSourceID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	entry, ok := s.byMedia[key]
	if !ok || entry.ExpireAt.Before(now) {
		s.cleanupLocked(now)
		return false
	}
	remain := entry.ExpireAt.Sub(now)
	if remain > capRemaining {
		s.cleanupLocked(now)
		return false
	}
	newExp := entry.ExpireAt.Add(add)
	maxExp := now.Add(capRemaining)
	if newExp.After(maxExp) {
		newExp = maxExp
	}
	if newExp.After(entry.ExpireAt) {
		entry.ExpireAt = newExp
		entry.Target.ExpireAt = newExp
		s.byMedia[key] = entry
	}
	s.cleanupLocked(now)
	return true
}

func clonePlaybackTarget(in PlaybackStreamTarget) PlaybackStreamTarget {
	out := in
	out.FinalHeaders = copyStringMap(in.FinalHeaders)
	out.Offers = clonePlaybackOffers(in.Offers)
	return out
}

func clonePlaybackOffers(in []smart.PlaybackOffer) []smart.PlaybackOffer {
	if len(in) == 0 {
		return nil
	}
	out := make([]smart.PlaybackOffer, 0, len(in))
	for _, offer := range in {
		out = append(out, clonePlaybackOffer(offer))
	}
	return out
}

func clonePlaybackOffer(in smart.PlaybackOffer) smart.PlaybackOffer {
	out := in
	if len(in.AccessByShare) > 0 {
		out.AccessByShare = make(map[string]string, len(in.AccessByShare))
		for k, v := range in.AccessByShare {
			out.AccessByShare[k] = v
		}
	}
	return out
}
