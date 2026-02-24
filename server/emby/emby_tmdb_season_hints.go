package emby

import "github.com/jenfonro/meowfilm/internal/db"

// ProbeDoubanTMDBSeasonHints probes Douban using the given keyword and stores
// multi-season episode-count hints into the database (best-effort).
//
// It returns the season hints found (may be empty) and whether probing succeeded.
func ProbeDoubanTMDBSeasonHints(database *db.DB, tmdbID int, keyword string) ([]db.TMDBSeasonHint, bool) {
	seasons, ok := doubanProbeSeasons(database, tmdbID, keyword, 0)
	if !ok || len(seasons) == 0 {
		return nil, ok
	}
	out := make([]db.TMDBSeasonHint, 0, len(seasons))
	for _, s := range seasons {
		if s.Season <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		out = append(out, db.TMDBSeasonHint{SeasonNumber: s.Season, EpisodeCount: s.EpisodeCount})
	}
	return out, true
}
