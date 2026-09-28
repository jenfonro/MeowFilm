package emby_service

import (
	"fmt"
	"strings"
	"sync"
	"time"
)

type PlayingGuardEntry struct {
	ExpireAt     time.Time
	ItemID       string
	NowItemName  string
	SeriesName   string
	SeasonNumber int
	EpisodeNo    int
}

var playingGuard = struct {
	sync.Mutex
	M map[string]PlayingGuardEntry
}{
	M: map[string]PlayingGuardEntry{},
}

const PlayingGuardTTL = 2 * time.Minute

func playingGuardKey(userID int64, deviceID string) string {
	return fmt.Sprintf("%d|%s", userID, strings.TrimSpace(deviceID))
}

func NotePlayingProgress(userID int64, deviceID string, entry PlayingGuardEntry) {
	if userID <= 0 || strings.TrimSpace(entry.ItemID) == "" {
		return
	}
	if entry.ExpireAt.IsZero() {
		entry.ExpireAt = time.Now().Add(PlayingGuardTTL)
	}
	key := playingGuardKey(userID, deviceID)
	playingGuard.Lock()
	playingGuard.M[key] = entry
	playingGuard.Unlock()
}

func ClearPlaying(userID int64, deviceID string) {
	if userID <= 0 {
		return
	}
	key := playingGuardKey(userID, deviceID)
	playingGuard.Lock()
	delete(playingGuard.M, key)
	playingGuard.Unlock()
}

func GetPlaying(userID int64, deviceID string) (PlayingGuardEntry, bool) {
	if userID <= 0 {
		return PlayingGuardEntry{}, false
	}
	key := playingGuardKey(userID, deviceID)
	now := time.Now()
	playingGuard.Lock()
	defer playingGuard.Unlock()
	entry, ok := playingGuard.M[key]
	if !ok {
		return PlayingGuardEntry{}, false
	}
	if !entry.ExpireAt.IsZero() && entry.ExpireAt.Before(now) {
		delete(playingGuard.M, key)
		return PlayingGuardEntry{}, false
	}
	return entry, true
}

func IsDerivedFromPlaying(playingItemID string, requestedItemID string) bool {
	p := strings.TrimSpace(playingItemID)
	r := strings.TrimSpace(requestedItemID)
	if p == "" || r == "" {
		return false
	}
	if strings.EqualFold(p, r) {
		return true
	}
	pp := parseItemRefAny(p)
	rp := parseItemRefAny(r)
	if pp == nil || rp == nil || pp.Source != rp.Source {
		return false
	}
	if pp.Source == "tmdb" && pp.NumericID > 0 && pp.NumericID == rp.NumericID {
		switch pp.SubKind {
		case "episode":
			switch rp.SubKind {
			case "episode":
				return pp.Pan == rp.Pan && pp.Episode == rp.Episode
			case "season":
				return pp.Pan == rp.Pan
			case "series":
				return true
			}
		case "season":
			switch rp.SubKind {
			case "episode":
				return pp.Pan == rp.Pan
			case "season":
				return pp.Pan == rp.Pan
			case "series":
				return true
			}
		case "series":
			return rp.SubKind == "series"
		case "movie":
			return rp.SubKind == "movie"
		}
	}
	if pp.Source == "site" && pp.SiteKey == rp.SiteKey && pp.SiteDetail == rp.SiteDetail {
		switch pp.SubKind {
		case "episode":
			switch rp.SubKind {
			case "episode":
				return pp.Pan == rp.Pan && pp.Episode == rp.Episode
			case "season":
				return pp.Pan == rp.Pan
			case "series":
				return true
			}
		case "season":
			switch rp.SubKind {
			case "episode":
				return pp.Pan == rp.Pan
			case "season":
				return pp.Pan == rp.Pan
			case "series":
				return true
			}
		case "series":
			return rp.SubKind == "series"
		}
	}
	return false
}

func IsDerivedSearchTerm(playing PlayingGuardEntry, searchTerm string) bool {
	q := foldGuardSearchTerm(searchTerm)
	if q == "" {
		return false
	}
	for _, cand := range []string{playing.SeriesName, playing.NowItemName} {
		ck := foldGuardSearchTerm(cand)
		if ck == "" {
			continue
		}
		if q == ck || (len(ck) >= 2 && strings.Contains(q, ck)) || (len(q) >= 2 && strings.Contains(ck, q)) {
			return true
		}
	}
	ref := parseItemRefAny(strings.TrimSpace(playing.ItemID))
	if ref != nil && ref.SubKind == "episode" && ref.Pan > 0 && ref.Episode > 0 {
		key := strings.ToLower(strings.ReplaceAll(q, "-", ""))
		want := fmt.Sprintf("s%02de%02d", ref.Pan, ref.Episode)
		want2 := fmt.Sprintf("s%de%d", ref.Pan, ref.Episode)
		return key == want || key == want2 || strings.Contains(key, want) || strings.Contains(key, want2)
	}
	return false
}

func foldGuardSearchTerm(s string) string {
	s = strings.ToLower(strings.TrimSpace(s))
	s = strings.ReplaceAll(s, " ", "")
	return s
}
