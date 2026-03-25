package emby_service

import (
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func BuildResumePayload(database *db.DB, userID int64, serverID string, limit int, startIndex int) (ResumeResponse, error) {
	if database == nil || userID <= 0 {
		return ResumeResponse{Items: EmptyAnySlice(), TotalRecordCount: 0}, nil
	}
	snaps, ids, err := database.ListResumePlaybackItems(userID, limit, startIndex)
	if err != nil {
		return ResumeResponse{}, err
	}
	items := make([]any, 0, len(ids))
	for i, itemID := range ids {
		if i >= len(snaps) {
			break
		}
		payload, ok, err := BuildResumePlaybackPayload(database, userID, serverID, itemID)
		if err != nil || !ok {
			continue
		}
		if patched, ok := applyResumeSnapshotToPayload(payload, snaps[i]); ok {
			items = append(items, patched)
		}
	}
	total, err := database.CountResumePlaybackItems(userID)
	if err != nil {
		return ResumeResponse{}, err
	}
	if total < startIndex+len(items) {
		total = startIndex + len(items)
	}
	return ResumeResponse{Items: items, TotalRecordCount: total}, nil
}

func BuildHideFromResumeResponse(database *db.DB, userID int64, itemID string, hide bool) (HideFromResumeResponse, error) {
	snap := db.PlayHistorySnapshot{}
	if m, err := database.GetPlayHistorySnapshotsByPlaybackItemIDs(userID, []string{itemID}); err == nil {
		if v, ok := m[itemID]; ok {
			snap = v
		}
	}
	if hide {
		if _, err := database.DeleteResumePlaybackItem(userID, itemID); err != nil {
			return HideFromResumeResponse{}, err
		}
	}
	resp := HideFromResumeResponse{
		PlayedPercentage:      PlayedPercentage(snap.Pos, ResumeRuntime(snap)),
		PlaybackPositionTicks: maxInt64(0, snap.Pos),
		PlayCount:             0,
		IsFavorite:            false,
		LastPlayedDate:        "",
		Played:                false,
	}
	if snap.Updated > 0 {
		resp.LastPlayedDate = embyTimeString(time.Unix(snap.Updated, 0).UTC())
	}
	return resp, nil
}

func applyResumeSnapshotToPayload(payload any, snap db.PlayHistorySnapshot) (any, bool) {
	switch v := payload.(type) {
	case ResumeEpisodeItemDTO:
		runtime := ResumeRuntime(snap)
		v.RunTimeTicks = runtime
		v.UserData = ResumeEpisodeUserDataDTO{
			PlaybackPositionTicks: maxInt64(0, snap.Pos),
			PlayCount:             0,
			IsFavorite:            false,
			Played:                false,
			PlayedPercentage:      PlayedPercentagePtr(snap.Pos, runtime),
		}
		return v, true
	case ResumeMovieItemDTO:
		runtime := ResumeRuntime(snap)
		v.RunTimeTicks = runtime
		v.UserData = MovieLatestUserDataDTO{
			PlaybackPositionTicks: maxInt64(0, snap.Pos),
			PlayCount:             0,
			IsFavorite:            false,
			Played:                false,
			PlayedPercentage:      PlayedPercentagePtr(snap.Pos, runtime),
		}
		if len(v.MediaSources) > 0 {
			v.MediaSources[0].RunTimeTicks = runtime
		}
		return v, true
	default:
		return nil, false
	}
}
