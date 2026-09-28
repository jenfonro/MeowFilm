package emby_service

import (
	"math"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const defaultPosterAspectRatio = 0.6666667
const nextUpPrimaryAspectRatio = 1.7781282860147214

func PosterAspectRatio(raw string) float64 {
	s := strings.TrimSpace(raw)
	if s == "" {
		return defaultPosterAspectRatio
	}
	switch {
	case strings.Contains(s, "x426"):
		return defaultPosterAspectRatio
	case strings.Contains(s, "x1000"):
		return defaultPosterAspectRatio
	default:
		return defaultPosterAspectRatio
	}
}

func NormalizeAspectRatio(v float64) float64 {
	if v <= 0 || math.IsNaN(v) || math.IsInf(v, 0) {
		return defaultPosterAspectRatio
	}
	return v
}

func ParentLogoImageTag(parentID string) string {
	return LogoTagForItem(parentID)
}

func SeriesPrimaryImageTag(seriesID string) string {
	return PrimaryTagForItem(seriesID)
}

func BuildMovieDetailUserData(row *db.PlayHistoryRow) MovieDetailUserDataDTO {
	out := MovieDetailUserDataDTO{
		PlaybackPositionTicks: 0,
		PlayCount:             0,
		IsFavorite:            false,
		LastPlayedDate:        "",
		Played:                false,
	}
	if row == nil {
		return out
	}
	out.PlaybackPositionTicks = maxInt64(0, row.PlaybackPositionTicks)
	if row.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	if row.UpdatedAt > 0 {
		out.LastPlayedDate = embyTimeString(time.Unix(row.UpdatedAt, 0))
	}
	runtime := maxInt64(0, row.PlaybackRuntimeTicks)
	if runtime <= 0 && row.PlaybackPositionTicks > 0 {
		runtime = row.PlaybackPositionTicks + int64(60*10_000_000)
	}
	if runtime > 0 && row.PlaybackPositionTicks > 0 {
		p := float64(row.PlaybackPositionTicks) * 100 / float64(runtime)
		out.PlayedPercentage = &p
	}
	return out
}

func BuildSeriesUserData(row *db.PlayHistoryRow) TVLatestUserDataDTO {
	out := EmptyTVLatestUserData()
	if row == nil {
		return out
	}
	out.PlaybackPositionTicks = maxInt64(0, row.PlaybackPositionTicks)
	if row.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	return out
}

func BuildEpisodeSimpleUserData(row *db.PlayHistoryRow) SimpleUserDataDTO {
	out := EmptySimpleUserData()
	if row == nil {
		return out
	}
	out.PlaybackPositionTicks = maxInt64(0, row.PlaybackPositionTicks)
	if row.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	return out
}

func BuildNextUpUserData(row *db.PlayHistoryRow) NextUpUserDataDTO {
	out := NextUpUserDataDTO{
		PlayedPercentage:      0,
		PlaybackPositionTicks: 0,
		PlayCount:             0,
		IsFavorite:            false,
		Played:                false,
	}
	if row == nil {
		return out
	}
	out.PlaybackPositionTicks = maxInt64(0, row.PlaybackPositionTicks)
	if out.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	runtime := maxInt64(0, row.PlaybackRuntimeTicks)
	if runtime <= 0 && row.PlaybackPositionTicks > 0 {
		runtime = row.PlaybackPositionTicks + int64(60*10_000_000)
	}
	if runtime > 0 && row.PlaybackPositionTicks > 0 {
		out.PlayedPercentage = float64(row.PlaybackPositionTicks) * 100 / float64(runtime)
	}
	return out
}

func BuildNextUpUserDataFromSnapshot(snap db.PlayHistorySnapshot) NextUpUserDataDTO {
	out := NextUpUserDataDTO{
		PlayedPercentage:      0,
		PlaybackPositionTicks: maxInt64(0, snap.Pos),
		PlayCount:             0,
		IsFavorite:            false,
		Played:                false,
	}
	if out.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	runtime := ResumeRuntime(snap)
	if runtime > 0 && snap.Pos > 0 {
		out.PlayedPercentage = float64(snap.Pos) * 100 / float64(runtime)
	}
	return out
}

func BuildEpisodeSimpleUserDataFromSnapshot(snap db.PlayHistorySnapshot) SimpleUserDataDTO {
	out := EmptySimpleUserData()
	out.PlaybackPositionTicks = maxInt64(0, snap.Pos)
	if out.PlaybackPositionTicks > 0 {
		out.PlayCount = 1
	}
	return out
}
