package emby_service

import "github.com/jenfonro/meowfilm/internal/db"

func playHistoryMatchesTMDBEpisode(row *db.PlayHistoryRow, tmdbID int, seasonNo int, episodeNo int) bool {
	if row == nil || tmdbID <= 0 || seasonNo <= 0 || episodeNo <= 0 {
		return false
	}
	if row.TMDBID != tmdbID {
		return false
	}
	return row.TMDBSeason == seasonNo && row.TMDBEpisode == episodeNo
}
