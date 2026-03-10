package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

type embyTMDBSearchItem = smart.TMDBSearchItem
type embyTMDBTVDetail = smart.TMDBTVDetail
type embyTMDBMovieDetail = smart.TMDBMovieDetail
type embyTMDBCredits = smart.TMDBCredits
type embyTMDBCast = smart.TMDBCast
type embyTMDBCrew = smart.TMDBCrew
type embyTMDBTVSeasonDetail = smart.TMDBTVSeasonDetail
type embyTMDBSeasonEpisode = smart.TMDBSeasonEpisode

func embyTMDBImageURL(database *db.DB, path string, size string) string {
	return smart.TMDBImageURL(database, path, size)
}

func embyTMDBSearchMulti(database *db.DB, query string) ([]embyTMDBSearchItem, error) {
	return smart.TMDBSearchMulti(database, query)
}

func embyTMDBDiscover(database *db.DB, mediaType string, yearStart int, yearEnd int, sortBy string, page int) (items []embyTMDBSearchItem, total int, err error) {
	return smart.TMDBDiscover(database, mediaType, yearStart, yearEnd, sortBy, page)
}

func embyTMDBGetTVDetail(database *db.DB, tmdbID int) (*embyTMDBTVDetail, error) {
	return smart.TMDBGetTVDetail(database, tmdbID)
}

func embyTMDBGetTVSeasonDetail(database *db.DB, tmdbID int, season int) (*embyTMDBTVSeasonDetail, error) {
	return smart.TMDBGetTVSeasonDetail(database, tmdbID, season)
}

func embyTMDBGetTVSeasonDetailAtLeast(database *db.DB, tmdbID int, season int, minEpisodes int) (*embyTMDBTVSeasonDetail, error) {
	return smart.TMDBGetTVSeasonDetailAtLeast(database, tmdbID, season, minEpisodes)
}

func embyTMDBGetTVSeasonEpisodes(database *db.DB, tmdbID int, season int) ([]embyTMDBSeasonEpisode, error) {
	return smart.TMDBGetTVSeasonEpisodes(database, tmdbID, season)
}

func embyTMDBGetTVSeasonEpisodesAtLeast(database *db.DB, tmdbID int, season int, minEpisodes int) ([]embyTMDBSeasonEpisode, error) {
	return smart.TMDBGetTVSeasonEpisodesAtLeast(database, tmdbID, season, minEpisodes)
}

func embyTMDBGetMovieDetail(database *db.DB, tmdbID int) (*embyTMDBMovieDetail, error) {
	return smart.TMDBGetMovieDetail(database, tmdbID)
}

func embyTMDBGetCredits(database *db.DB, mediaType string, tmdbID int) (*embyTMDBCredits, error) {
	return smart.TMDBGetCredits(database, mediaType, tmdbID)
}

func embyTMDBGetPersonProfile(database *db.DB, personID int) (string, error) {
	return smart.TMDBGetPersonProfile(database, personID)
}

func embyRememberPersonProfile(personID int, profilePath string) {
	smart.RememberPersonProfile(personID, profilePath)
}
