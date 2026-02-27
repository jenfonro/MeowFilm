package emby

import (
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyPlayHistorySnapshot struct {
	Pos     int64
	Runtime int64
	Updated int64
}

func embyQueryPlayHistoryByVideoID(database *db.DB, userID string, videoID string) (embyPlayHistorySnapshot, bool) {
	if database == nil {
		return embyPlayHistorySnapshot{}, false
	}
	uidRaw := strings.TrimSpace(userID)
	uid, _ := strconv.ParseInt(uidRaw, 10, 64)
	vid := strings.TrimSpace(videoID)
	if uid <= 0 || vid == "" {
		return embyPlayHistorySnapshot{}, false
	}

	// Prefer contentKey-based lookup for TMDB series/movie ids so play history created by the web UI
	// (siteKey != "emby") can still drive Emby "resume/next up" behaviors.
	if parsed, ok := embyParseItemID(vid); ok && parsed != nil && parsed.Source == "tmdb" && parsed.TMDBID > 0 {
		if parsed.Kind == "tv" && parsed.SubKind == "series" {
			key := "tmdb:tv:" + strconv.Itoa(parsed.TMDBID)
			if row, err := database.GetPlayHistoryLatestByContentKey(uid, key); err == nil && row != nil {
				return embyPlayHistorySnapshot{
					Pos:     row.PlaybackPositionTicks,
					Runtime: row.PlaybackRuntimeTicks,
					Updated: row.UpdatedAt,
				}, true
			}
		}
		if parsed.Kind == "movie" && parsed.SubKind == "movie" {
			key := "tmdb:movie:" + strconv.Itoa(parsed.TMDBID)
			if row, err := database.GetPlayHistoryLatestByContentKey(uid, key); err == nil && row != nil {
				return embyPlayHistorySnapshot{
					Pos:     row.PlaybackPositionTicks,
					Runtime: row.PlaybackRuntimeTicks,
					Updated: row.UpdatedAt,
				}, true
			}
		}
	}

	// Fallback for legacy Emby-only rows keyed by a synthetic site_video (siteKey="emby", videoID=itemID).
	snap, ok := database.GetPlayHistorySnapshotBySiteVideo(uid, "emby", vid)
	if !ok {
		return embyPlayHistorySnapshot{}, false
	}
	return embyPlayHistorySnapshot{Pos: snap.Pos, Runtime: snap.Runtime, Updated: snap.Updated}, true
}

func embyQueryPlayHistoryByItemIDs(database *db.DB, userID string, itemIDs []string) map[string]embyPlayHistorySnapshot {
	out := map[string]embyPlayHistorySnapshot{}
	if database == nil {
		return out
	}
	uidRaw := strings.TrimSpace(userID)
	uid, _ := strconv.ParseInt(uidRaw, 10, 64)
	if uid <= 0 || len(itemIDs) == 0 {
		return out
	}
	uniq := make([]string, 0, len(itemIDs))
	seen := map[string]struct{}{}
	for _, id := range itemIDs {
		k := strings.TrimSpace(id)
		if k == "" {
			continue
		}
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		uniq = append(uniq, k)
	}
	if len(uniq) == 0 {
		return out
	}

	m, err := database.GetPlayHistorySnapshotsByPlaybackItemIDs(uid, uniq)
	if err != nil {
		return out
	}
	for id, s := range m {
		out[id] = embyPlayHistorySnapshot{Pos: s.Pos, Runtime: s.Runtime, Updated: s.Updated}
	}
	return out
}

func embyApplyPlayHistoryToItemUserData(userID string, itemID string, obj map[string]any, snap embyPlayHistorySnapshot) {
	if obj == nil {
		return
	}
	id := strings.TrimSpace(itemID)
	uid := strings.TrimSpace(userID)
	if id == "" || uid == "" {
		return
	}
	if snap.Pos <= 0 && snap.Runtime <= 0 && snap.Updated <= 0 {
		return
	}

	embyEnsureStandardUserData(obj)
	ud, _ := obj["UserData"].(map[string]any)
	if ud == nil {
		ud = map[string]any{}
	}
	pos := snap.Pos
	if pos < 0 {
		pos = 0
	}
	ud["PlaybackPositionTicks"] = pos
	if snap.Runtime > 0 && pos > 0 {
		ud["PlayedPercentage"] = (float64(pos) / float64(snap.Runtime)) * 100.0
	}
	if snap.Updated > 0 {
		ud["LastPlayedDate"] = time.Unix(snap.Updated, 0).UTC().Format(time.RFC3339Nano)
	}
	if _, ok := ud["Key"]; !ok || strings.TrimSpace(embyAnyToString(ud["Key"])) == "" {
		ud["Key"] = embyStableKeyDigits(uid + ":" + id)
	}
	obj["UserData"] = ud
}
