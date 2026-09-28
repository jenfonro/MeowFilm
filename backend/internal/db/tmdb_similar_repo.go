package db

import (
	"database/sql"
	"errors"
	"strings"
)

// ListTMDBCachedDetailsByType returns a shallow local-only candidate pool from tmdb_media.
// It intentionally does not trigger refresh or load season/episode expansions.
func (d *DB) ListTMDBCachedDetailsByType(tmdbType string, lang string, limit int) ([]TMDBCachedDetail, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if typ != "tv" && typ != "movie" {
		return nil, errors.New("invalid args")
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}
	if limit <= 0 {
		limit = 200
	}
	rows, err := d.db.Query(`
		SELECT m.tmdb_id, m.adult, m.original_language, m.genre_ids_json, m.origin_country_json,
		       m.popularity, m.vote_average, m.vote_count,
		       m.poster_path, m.backdrop_path, m.status, m.in_production,
		       m.next_episode_air_date, m.next_episode_refresh_day,
		       m.first_air_date, m.release_date, m.runtime,
		       m.meta_level, m.season_level, m.last_access_at, m.last_refresh_at,
		       COALESCE(i.title,''), COALESCE(i.overview,''),
		       COALESCE(sc.total_episodes, 0)
		FROM tmdb_media m
		LEFT JOIN tmdb_media_i18n i
		  ON i.media_id = m.id AND i.lang = ?
		LEFT JOIN (
			SELECT media_id, SUM(CASE WHEN season_number > 0 THEN episode_count ELSE 0 END) AS total_episodes
			FROM tmdb_season
			GROUP BY media_id
		) sc ON sc.media_id = m.id
		WHERE m.tmdb_type = ?
		ORDER BY m.last_refresh_at DESC, m.last_access_at DESC, m.tmdb_id DESC
		LIMIT ?
	`, l, typ, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := make([]TMDBCachedDetail, 0, limit)
	for rows.Next() {
		var (
			item              TMDBCachedDetail
			adult             int
			genreIDsJSON      string
			originCountryJSON string
			inProduction      int
			title             string
			overview          string
			runtimeMinutes    int
			episodeCount      sql.NullInt64
		)
		if err := rows.Scan(
			&item.TMDBID, &adult, &item.OriginalLanguage, &genreIDsJSON, &originCountryJSON,
			&item.Popularity, &item.VoteAverage, &item.VoteCount,
			&item.PosterPath, &item.Backdrop, &item.Status, &inProduction,
			&item.NextEpisodeAirDate, &item.NextEpisodeRefreshDay,
			&item.FirstAir, &item.Release, &runtimeMinutes,
			&item.MetaLevel, &item.SeasonLevel, &item.LastAccessAt, &item.LastRefreshAt,
			&title, &overview, &episodeCount,
		); err != nil {
			return nil, err
		}
		item.TMDBType = typ
		item.Adult = adult != 0
		item.GenreIDs = parseIntJSONArray(genreIDsJSON)
		item.OriginCountry = parseStringJSONArray(originCountryJSON)
		item.InProduction = inProduction != 0
		item.Title = strings.TrimSpace(title)
		item.Overview = strings.TrimSpace(overview)
		item.RuntimeMinutes = runtimeMinutes
		if episodeCount.Valid && episodeCount.Int64 > 0 {
			item.EpisodeCount = int(episodeCount.Int64)
		}
		out = append(out, item)
	}
	return out, rows.Err()
}
