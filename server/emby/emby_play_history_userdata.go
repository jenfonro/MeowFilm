package emby

import (
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyPlayHistorySnapshot struct {
	Pos     int64
	Runtime int64
	Updated int64
}

func embyQueryPlayHistoryByItemIDs(database *db.DB, userID string, itemIDs []string) map[string]embyPlayHistorySnapshot {
	out := map[string]embyPlayHistorySnapshot{}
	if database == nil {
		return out
	}
	uid := strings.TrimSpace(userID)
	if uid == "" || len(itemIDs) == 0 {
		return out
	}
	uniq := make([]string, 0, len(itemIDs))
	seen := map[string]bool{}
	for _, id := range itemIDs {
		k := strings.TrimSpace(id)
		if k == "" || seen[k] {
			continue
		}
		seen[k] = true
		uniq = append(uniq, k)
	}
	if len(uniq) == 0 {
		return out
	}

	placeholders := strings.Repeat("?,", len(uniq))
	placeholders = strings.TrimSuffix(placeholders, ",")
	args := make([]any, 0, 1+len(uniq))
	args = append(args, uid)
	for _, id := range uniq {
		args = append(args, id)
	}

	rows, err := database.SQL().Query(
		`SELECT playback_item_id, playback_position_ticks, playback_runtime_ticks, updated_at
		 FROM play_history
		 WHERE user_id = ? AND playback_item_id IN (`+placeholders+`)`,
		args...,
	)
	if err != nil {
		return out
	}
	defer rows.Close()

	for rows.Next() {
		var (
			itemID  string
			pos     int64
			runtime int64
			updated int64
		)
		_ = rows.Scan(&itemID, &pos, &runtime, &updated)
		itemID = strings.TrimSpace(itemID)
		if itemID == "" {
			continue
		}
		out[itemID] = embyPlayHistorySnapshot{Pos: pos, Runtime: runtime, Updated: updated}
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
