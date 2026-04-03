package emby_service

import (
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func TestResolveTVSeriesProgressCursorUsesPlaybackItemID(t *testing.T) {
	hist := &db.PlayHistoryRow{
		PlaybackItemID: buildEpisodeID(100, 2, 16),
		TMDBSeason:     1,
		TMDBEpisode:    3,
		PreOrder:       true,
	}
	got := resolveTVSeriesProgressCursor(hist, 100)
	if got.Season != 2 || got.Episode != 16 || !got.IncludeUnaired {
		t.Fatalf("got cursor %+v", got)
	}
}

func TestResolveTVSeriesProgressCursorFallsBackToHistoryFields(t *testing.T) {
	hist := &db.PlayHistoryRow{
		PlaybackItemID: "bad-id",
		TMDBSeason:     2,
		TMDBEpisode:    8,
	}
	got := resolveTVSeriesProgressCursor(hist, 100)
	if got.Season != 2 || got.Episode != 8 || got.IncludeUnaired {
		t.Fatalf("got cursor %+v", got)
	}
}

func TestBuildFollowingCandidatesFromSeasonSkipsCurrentEpisode(t *testing.T) {
	now := time.Now()
	episodes := []db.TMDBCachedSeasonEpisode{
		{SeasonNumber: 2, EpisodeNumber: 16, AirDate: now.Add(-24 * time.Hour).Format("2006-01-02")},
		{SeasonNumber: 2, EpisodeNumber: 17, AirDate: now.Add(-24 * time.Hour).Format("2006-01-02")},
	}
	got := buildFollowingCandidatesFromSeason(2, "Season 2", episodes, 2, 16, 0)
	if len(got) != 1 || got[0].Episode.EpisodeNumber != 17 {
		t.Fatalf("got candidates %+v", got)
	}
}

func TestBuildFollowingCandidatesPreOrderOffExcludesUnaired(t *testing.T) {
	now := time.Now()
	episodes := filterSeasonEpisodesForNextUp([]db.TMDBCachedSeasonEpisode{
		{SeasonNumber: 2, EpisodeNumber: 16, AirDate: now.Add(-24 * time.Hour).Format("2006-01-02")},
		{SeasonNumber: 2, EpisodeNumber: 17, AirDate: now.Add(24 * time.Hour).Format("2006-01-02")},
	}, false, now)
	got := buildFollowingCandidatesFromSeason(2, "Season 2", episodes, 2, 16, 0)
	if len(got) != 0 {
		t.Fatalf("got candidates %+v", got)
	}
}

func TestBuildFollowingCandidatesPreOrderOnIncludesUnaired(t *testing.T) {
	now := time.Now()
	episodes := filterSeasonEpisodesForNextUp([]db.TMDBCachedSeasonEpisode{
		{SeasonNumber: 2, EpisodeNumber: 16, AirDate: now.Add(-24 * time.Hour).Format("2006-01-02")},
		{SeasonNumber: 2, EpisodeNumber: 17, AirDate: now.Add(24 * time.Hour).Format("2006-01-02")},
	}, true, now)
	got := buildFollowingCandidatesFromSeason(2, "Season 2", episodes, 2, 16, 0)
	if len(got) != 1 || got[0].Episode.EpisodeNumber != 17 {
		t.Fatalf("got candidates %+v", got)
	}
}
