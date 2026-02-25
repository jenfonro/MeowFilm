package emby

import (
	"fmt"
	"strings"
	"sync"
	"time"
)

type embyPlayingGuardEntry struct {
	ExpireAt     time.Time
	ItemID       string
	NowItemName  string
	SeriesName   string
	SeasonNumber int
	EpisodeNo    int
}

var embyPlayingGuard = struct {
	sync.Mutex
	M map[string]embyPlayingGuardEntry
}{
	M: map[string]embyPlayingGuardEntry{},
}

const embyPlayingGuardTTL = 2 * time.Minute

func embyPlayingGuardKey(userID string, deviceID string) string {
	return strings.TrimSpace(userID) + "|" + strings.TrimSpace(deviceID)
}

func embyNotePlayingProgress(userID string, deviceID string, e embyPlayingGuardEntry) {
	if strings.TrimSpace(userID) == "" {
		return
	}
	if strings.TrimSpace(e.ItemID) == "" {
		return
	}
	if e.ExpireAt.IsZero() {
		e.ExpireAt = time.Now().Add(embyPlayingGuardTTL)
	}
	key := embyPlayingGuardKey(userID, deviceID)
	embyPlayingGuard.Lock()
	defer embyPlayingGuard.Unlock()
	embyPlayingGuard.M[key] = e
}

func embyClearPlaying(userID string, deviceID string) {
	if strings.TrimSpace(userID) == "" {
		return
	}
	key := embyPlayingGuardKey(userID, deviceID)
	embyPlayingGuard.Lock()
	defer embyPlayingGuard.Unlock()
	delete(embyPlayingGuard.M, key)
}

func embyGetPlaying(userID string, deviceID string) (embyPlayingGuardEntry, bool) {
	if strings.TrimSpace(userID) == "" {
		return embyPlayingGuardEntry{}, false
	}
	key := embyPlayingGuardKey(userID, deviceID)
	now := time.Now()
	embyPlayingGuard.Lock()
	defer embyPlayingGuard.Unlock()
	e, ok := embyPlayingGuard.M[key]
	if !ok {
		return embyPlayingGuardEntry{}, false
	}
	if !e.ExpireAt.IsZero() && e.ExpireAt.Before(now) {
		delete(embyPlayingGuard.M, key)
		return embyPlayingGuardEntry{}, false
	}
	return e, true
}

func embyIsDerivedFromPlaying(playingItemID string, requestedItemID string) bool {
	p := strings.TrimSpace(playingItemID)
	r := strings.TrimSpace(requestedItemID)
	if p == "" || r == "" {
		return false
	}
	if strings.EqualFold(p, r) {
		return true
	}
	pp, ok1 := embyParseItemID(p)
	rp, ok2 := embyParseItemID(r)
	if !ok1 || !ok2 || pp == nil || rp == nil {
		return false
	}
	if pp.TMDBID <= 0 || rp.TMDBID <= 0 || pp.TMDBID != rp.TMDBID {
		return false
	}

	// Only short-circuit requests that are plausibly "derived" from the exact playing item:
	// - same episode, its season, or its series
	// - same season, its series, or episodes within that season
	// We intentionally avoid suppressing unrelated items that share the same TMDB ID (e.g. other seasons/episodes).
	switch pp.SubKind {
	case "episode":
		switch rp.SubKind {
		case "episode":
			return pp.Season == rp.Season && pp.Episode == rp.Episode
		case "season":
			return pp.Season == rp.Season
		case "series":
			return true
		default:
			return false
		}
	case "season":
		switch rp.SubKind {
		case "episode":
			return pp.Season == rp.Season
		case "season":
			return pp.Season == rp.Season
		case "series":
			return true
		default:
			return false
		}
	case "series":
		// If a client reports "playing" at series level (rare), only treat the series itself as derived.
		return rp.SubKind == "series"
	default:
		// Movies: only the exact item id is considered derived (handled above).
		return false
	}
}

func embyIsDerivedSearchTerm(playing embyPlayingGuardEntry, searchTerm string) bool {
	q := strings.TrimSpace(searchTerm)
	if q == "" {
		return false
	}

	fold := func(s string) string {
		s = CanonicalSearchTerm(s)
		s = strings.ToLower(strings.TrimSpace(s))
		s = strings.ReplaceAll(s, " ", "")
		return s
	}

	qk := fold(q)
	if qk == "" {
		return false
	}

	cands := []string{playing.SeriesName, playing.NowItemName}
	for _, c := range cands {
		ck := fold(c)
		if ck == "" {
			continue
		}
		if qk == ck {
			return true
		}
		// Best-effort contains match (avoid too-short tokens).
		if len(ck) >= 2 && strings.Contains(qk, ck) {
			return true
		}
		if len(qk) >= 2 && strings.Contains(ck, qk) {
			return true
		}
	}

	// Episode shorthand (SxxExx) is commonly used by background lookups.
	if p, ok := embyParseItemID(strings.TrimSpace(playing.ItemID)); ok && p != nil && p.SubKind == "episode" && p.Season > 0 && p.Episode > 0 {
		key := strings.ToLower(strings.ReplaceAll(qk, "-", ""))
		want := fmt.Sprintf("s%02de%02d", p.Season, p.Episode)
		want2 := fmt.Sprintf("s%de%d", p.Season, p.Episode)
		if key == want || key == want2 || strings.Contains(key, want) || strings.Contains(key, want2) {
			return true
		}
	}

	return false
}

func embyBuildQuickNowPlayingItem(serverID string, itemID string, playing embyPlayingGuardEntry) map[string]any {
	id := strings.TrimSpace(itemID)
	if id == "" {
		return nil
	}
	parsed, ok := embyParseItemID(id)
	if !ok || parsed == nil {
		return map[string]any{
			"Id":           id,
			"Name":         defaultString(playing.NowItemName, id),
			"Type":         "Video",
			"MediaType":    "Video",
			"IsFolder":     false,
			"LocationType": "Remote",
			"ServerId":     serverID,
			"UserData":     map[string]any{"Played": false},
		}
	}

	name := strings.TrimSpace(playing.NowItemName)
	seriesName := strings.TrimSpace(playing.SeriesName)
	if parsed.Kind == "tv" && parsed.SubKind == "episode" {
		if name == "" {
			if parsed.Season > 0 && parsed.Episode > 0 {
				name = fmt.Sprintf("S%02dE%02d", parsed.Season, parsed.Episode)
			} else {
				name = id
			}
		}
		if seriesName == "" {
			seriesName = id
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"SeriesName":        seriesName,
			"Type":              "Episode",
			"MediaType":         "Video",
			"IsFolder":          false,
			"LocationType":      "Remote",
			"ServerId":          serverID,
			"IndexNumber":       parsed.Episode,
			"ParentIndexNumber": parsed.Season,
			"UserData":          map[string]any{"Played": false},
			"ImageTags":         map[string]any{},
			"BackdropImageTags": []any{},
		}
	}
	if parsed.Kind == "tv" && parsed.SubKind == "series" {
		if seriesName != "" {
			name = seriesName
		}
		if name == "" {
			name = id
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"Type":              "Series",
			"MediaType":         "Video",
			"IsFolder":          true,
			"LocationType":      "Remote",
			"ServerId":          serverID,
			"UserData":          map[string]any{"Played": false},
			"ImageTags":         map[string]any{},
			"BackdropImageTags": []any{},
		}
	}
	if parsed.Kind == "tv" && parsed.SubKind == "season" {
		if name == "" {
			if parsed.Season > 0 {
				name = fmt.Sprintf("第%s季", intToCN(parsed.Season))
			} else {
				name = id
			}
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"SeriesName":        seriesName,
			"Type":              "Season",
			"MediaType":         "Video",
			"IsFolder":          true,
			"LocationType":      "Remote",
			"ServerId":          serverID,
			"IndexNumber":       parsed.Season,
			"UserData":          map[string]any{"Played": false},
			"ImageTags":         map[string]any{},
			"BackdropImageTags": []any{},
		}
	}
	if parsed.Kind == "movie" {
		if name == "" {
			name = id
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"Type":              "Movie",
			"MediaType":         "Video",
			"IsFolder":          false,
			"LocationType":      "Remote",
			"ServerId":          serverID,
			"UserData":          map[string]any{"Played": false},
			"ImageTags":         map[string]any{},
			"BackdropImageTags": []any{},
		}
	}
	if name == "" {
		name = id
	}
	return map[string]any{
		"Id":           id,
		"Name":         name,
		"Type":         "Video",
		"MediaType":    "Video",
		"IsFolder":     false,
		"LocationType": "Remote",
		"ServerId":     serverID,
		"UserData":     map[string]any{"Played": false},
	}
}
