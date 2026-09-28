package db

import (
	"database/sql"
	"encoding/json"
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"
)

type TMDBSeason struct {
	TMDBSeasonID int
	SeasonNumber int
	EpisodeCount int
	DetailJSON   string
	AirDate      string
	PosterPath   string
	Name         string
	Overview     string
	RefreshOnDay string
	LastSyncOKAt int64
}

type TMDBTVSeasonCountRow struct {
	SeasonNumber int
	EpisodeCount int
}

type TMDBEpisode struct {
	SeasonNumber  int
	EpisodeNumber int
	AirDate       string
	Runtime       int
	StillPath     string
	Name          string
	Overview      string
}

type TMDBUpsertMedia struct {
	Type                  string // tv|movie
	ID                    int
	Lang                  string // zh-CN
	SearchJSON            string
	DetailJSON            string
	Adult                 bool
	OriginalLanguage      string
	GenreIDsJSON          string
	OriginCountryJSON     string
	Popularity            float64
	VoteAverage           float64
	VoteCount             int
	Title                 string
	Original              string
	Overview              string
	Tagline               string
	Status                string
	InProduction          bool
	PosterPath            string
	BackdropPath          string
	NextEpisodeAirDate    string
	NextEpisodeRefreshDay string
	FirstAirDate          string
	ReleaseDate           string
	Runtime               int
	MetaLevel             string
	SeasonLevel           string
	UpdatedAt             int64
}

type TMDBMediaCompleteness struct {
	MetaLevel    string
	SeasonLevel  string
	HasOverview  bool
	SeasonCount  int
	EpisodeCount int
}

func (d *DB) UpsertTMDBMedia(m TMDBUpsertMedia) (mediaRowID int64, err error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	typ := strings.TrimSpace(m.Type)
	if typ != "tv" && typ != "movie" {
		return 0, errors.New("invalid tmdb type")
	}
	if m.ID <= 0 {
		return 0, errors.New("invalid tmdb id")
	}
	lang := strings.TrimSpace(m.Lang)
	if lang == "" {
		lang = "zh-CN"
	}
	now := m.UpdatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}

	tx, err := d.db.Begin()
	if err != nil {
		return 0, err
	}
	defer func() { _ = tx.Rollback() }()

	metaLevel := normalizeTMDBMetaLevel(m.MetaLevel)
	seasonLevel := normalizeTMDBSeasonLevel(m.SeasonLevel)

	_, _ = tx.Exec(`
		INSERT INTO tmdb_media(tmdb_type, tmdb_id, search_json, detail_json, adult, original_language, genre_ids_json, origin_country_json, popularity, vote_average, vote_count, poster_path, backdrop_path, status, in_production, next_episode_air_date, next_episode_refresh_day, first_air_date, release_date, runtime, meta_level, season_level, last_access_at, last_refresh_at, updated_at)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(tmdb_type, tmdb_id) DO UPDATE SET
		  search_json = CASE WHEN excluded.search_json <> '' THEN excluded.search_json ELSE tmdb_media.search_json END,
		  detail_json = CASE WHEN excluded.detail_json <> '' THEN excluded.detail_json ELSE tmdb_media.detail_json END,
		  adult = CASE WHEN excluded.adult <> 0 THEN excluded.adult ELSE tmdb_media.adult END,
		  original_language = CASE WHEN excluded.original_language <> '' THEN excluded.original_language ELSE tmdb_media.original_language END,
		  genre_ids_json = CASE WHEN excluded.genre_ids_json <> '' AND excluded.genre_ids_json <> '[]' THEN excluded.genre_ids_json ELSE tmdb_media.genre_ids_json END,
		  origin_country_json = CASE WHEN excluded.origin_country_json <> '' AND excluded.origin_country_json <> '[]' THEN excluded.origin_country_json ELSE tmdb_media.origin_country_json END,
		  popularity = CASE WHEN excluded.popularity > 0 THEN excluded.popularity ELSE tmdb_media.popularity END,
		  vote_average = CASE WHEN excluded.vote_average > 0 THEN excluded.vote_average ELSE tmdb_media.vote_average END,
		  vote_count = CASE WHEN excluded.vote_count > 0 THEN excluded.vote_count ELSE tmdb_media.vote_count END,
		  poster_path = CASE WHEN excluded.poster_path <> '' THEN excluded.poster_path ELSE tmdb_media.poster_path END,
		  backdrop_path = CASE WHEN excluded.backdrop_path <> '' THEN excluded.backdrop_path ELSE tmdb_media.backdrop_path END,
		  status = CASE WHEN excluded.status <> '' THEN excluded.status ELSE tmdb_media.status END,
		  in_production = CASE WHEN excluded.in_production <> 0 THEN excluded.in_production ELSE tmdb_media.in_production END,
		  next_episode_air_date = CASE WHEN excluded.next_episode_air_date <> '' THEN excluded.next_episode_air_date ELSE tmdb_media.next_episode_air_date END,
		  next_episode_refresh_day = CASE WHEN excluded.next_episode_refresh_day <> '' THEN excluded.next_episode_refresh_day ELSE tmdb_media.next_episode_refresh_day END,
		  first_air_date = CASE WHEN excluded.first_air_date <> '' THEN excluded.first_air_date ELSE tmdb_media.first_air_date END,
		  release_date = CASE WHEN excluded.release_date <> '' THEN excluded.release_date ELSE tmdb_media.release_date END,
		  runtime = CASE WHEN excluded.runtime > 0 THEN excluded.runtime ELSE tmdb_media.runtime END,
		  meta_level = CASE
		    WHEN excluded.meta_level = 'detail' THEN 'detail'
		    WHEN excluded.meta_level = 'search' AND tmdb_media.meta_level = 'none' THEN 'search'
		    ELSE tmdb_media.meta_level
		  END,
		  season_level = CASE
		    WHEN excluded.season_level = 'episodes' THEN 'episodes'
		    WHEN excluded.season_level = 'summary' AND tmdb_media.season_level IN ('none', '') THEN 'summary'
		    WHEN excluded.season_level = 'search' AND tmdb_media.season_level = 'none' THEN 'search'
		    ELSE tmdb_media.season_level
		  END,
		  last_access_at = excluded.last_access_at,
		  last_refresh_at = excluded.last_refresh_at,
		  updated_at = excluded.updated_at
	`, typ, m.ID, strings.TrimSpace(m.SearchJSON), strings.TrimSpace(m.DetailJSON), boolToInt(m.Adult), strings.TrimSpace(m.OriginalLanguage), defaultJSONArray(strings.TrimSpace(m.GenreIDsJSON)), defaultJSONArray(strings.TrimSpace(m.OriginCountryJSON)), m.Popularity, m.VoteAverage, m.VoteCount, strings.TrimSpace(m.PosterPath), strings.TrimSpace(m.BackdropPath), strings.TrimSpace(m.Status), boolToInt(m.InProduction), strings.TrimSpace(m.NextEpisodeAirDate), strings.TrimSpace(m.NextEpisodeRefreshDay),
		strings.TrimSpace(m.FirstAirDate), strings.TrimSpace(m.ReleaseDate), m.Runtime, metaLevel, seasonLevel, now, now, now,
	)

	if err := tx.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type=? AND tmdb_id=? LIMIT 1`, typ, m.ID).Scan(&mediaRowID); err != nil {
		return 0, err
	}

	_, _ = tx.Exec(`
		INSERT INTO tmdb_media_i18n(media_id, lang, title, original_title, overview, tagline, updated_at)
		VALUES(?,?,?,?,?,?,?)
		ON CONFLICT(media_id, lang) DO UPDATE SET
		  title = CASE WHEN excluded.title <> '' THEN excluded.title ELSE tmdb_media_i18n.title END,
		  original_title = CASE WHEN excluded.original_title <> '' THEN excluded.original_title ELSE tmdb_media_i18n.original_title END,
		  overview = CASE WHEN excluded.overview <> '' THEN excluded.overview ELSE tmdb_media_i18n.overview END,
		  tagline = CASE WHEN excluded.tagline <> '' THEN excluded.tagline ELSE tmdb_media_i18n.tagline END,
		  updated_at = excluded.updated_at
	`, mediaRowID, lang, strings.TrimSpace(m.Title), strings.TrimSpace(m.Original), strings.TrimSpace(m.Overview), strings.TrimSpace(m.Tagline), now)

	if err := tx.Commit(); err != nil {
		return 0, err
	}
	return mediaRowID, nil
}

func (d *DB) UpsertTMDBSeasons(tmdbType string, tmdbID int, lang string, seasons []TMDBSeason) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if strings.TrimSpace(tmdbType) != "tv" || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	if len(seasons) == 0 {
		return nil
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}
	now := time.Now().Unix()

	var mediaRowID int64
	if err := d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type=? AND tmdb_id=? LIMIT 1`, "tv", tmdbID).Scan(&mediaRowID); err != nil {
		if err == sql.ErrNoRows {
			return nil
		}
		return err
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	for _, s := range seasons {
		if s.SeasonNumber < 0 {
			continue
		}
		_, _ = tx.Exec(`
			INSERT INTO tmdb_season(media_id, tmdb_season_id, season_number, detail_json, air_date, poster_path, episode_count, refresh_on_day, last_sync_ok_at, updated_at)
			VALUES(?,?,?,?,?,?,?,?,?,?)
			ON CONFLICT(media_id, season_number) DO UPDATE SET
			  tmdb_season_id = CASE WHEN excluded.tmdb_season_id > 0 THEN excluded.tmdb_season_id ELSE tmdb_season.tmdb_season_id END,
			  detail_json = CASE WHEN excluded.detail_json <> '' THEN excluded.detail_json ELSE tmdb_season.detail_json END,
			  air_date = CASE WHEN excluded.air_date <> '' THEN excluded.air_date ELSE tmdb_season.air_date END,
			  poster_path = CASE WHEN excluded.poster_path <> '' THEN excluded.poster_path ELSE tmdb_season.poster_path END,
			  episode_count = CASE WHEN excluded.episode_count > 0 THEN excluded.episode_count ELSE tmdb_season.episode_count END,
			  refresh_on_day = CASE WHEN excluded.refresh_on_day <> '' THEN excluded.refresh_on_day ELSE tmdb_season.refresh_on_day END,
			  last_sync_ok_at = CASE WHEN excluded.last_sync_ok_at > 0 THEN excluded.last_sync_ok_at ELSE tmdb_season.last_sync_ok_at END,
			  updated_at = excluded.updated_at
		`, mediaRowID, s.TMDBSeasonID, s.SeasonNumber, strings.TrimSpace(s.DetailJSON), strings.TrimSpace(s.AirDate), strings.TrimSpace(s.PosterPath), s.EpisodeCount, strings.TrimSpace(s.RefreshOnDay), s.LastSyncOKAt, now)

		var seasonID int64
		if err := tx.QueryRow(`SELECT id FROM tmdb_season WHERE media_id=? AND season_number=? LIMIT 1`, mediaRowID, s.SeasonNumber).Scan(&seasonID); err != nil {
			continue
		}
		if strings.TrimSpace(s.Name) != "" || strings.TrimSpace(s.Overview) != "" {
			_, _ = tx.Exec(`
				INSERT INTO tmdb_season_i18n(season_id, lang, name, overview, updated_at)
				VALUES(?,?,?,?,?)
				ON CONFLICT(season_id, lang) DO UPDATE SET
				  name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE tmdb_season_i18n.name END,
				  overview = CASE WHEN excluded.overview <> '' THEN excluded.overview ELSE tmdb_season_i18n.overview END,
				  updated_at = excluded.updated_at
			`, seasonID, l, strings.TrimSpace(s.Name), strings.TrimSpace(s.Overview), now)
		}
	}
	return tx.Commit()
}

func normalizeTMDBMetaLevel(raw string) string {
	switch strings.ToLower(strings.TrimSpace(raw)) {
	case "search":
		return "search"
	case "detail":
		return "detail"
	default:
		return "none"
	}
}

func normalizeTMDBSeasonLevel(raw string) string {
	switch strings.ToLower(strings.TrimSpace(raw)) {
	case "summary":
		return "summary"
	case "episodes":
		return "episodes"
	default:
		return "none"
	}
}

func boolToInt(v bool) int {
	if v {
		return 1
	}
	return 0
}

func defaultJSONArray(v string) string {
	if strings.TrimSpace(v) == "" {
		return "[]"
	}
	return v
}

func parseIntJSONArray(raw string) []int {
	s := strings.TrimSpace(raw)
	if s == "" {
		return []int{}
	}
	var out []int
	if err := json.Unmarshal([]byte(s), &out); err != nil || out == nil {
		return []int{}
	}
	return out
}

func parseStringJSONArray(raw string) []string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return []string{}
	}
	var out []string
	if err := json.Unmarshal([]byte(s), &out); err != nil || out == nil {
		return []string{}
	}
	return out
}

func (d *DB) UpsertTMDBEpisodes(tmdbID int, lang string, episodes []TMDBEpisode) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if tmdbID <= 0 {
		return errors.New("invalid args")
	}
	if len(episodes) == 0 {
		return nil
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}
	now := time.Now().Unix()

	var mediaRowID int64
	if err := d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type='tv' AND tmdb_id=? LIMIT 1`, tmdbID).Scan(&mediaRowID); err != nil {
		if err == sql.ErrNoRows {
			return nil
		}
		return err
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	for _, e := range episodes {
		if e.SeasonNumber < 0 || e.EpisodeNumber <= 0 {
			continue
		}
		_, _ = tx.Exec(`
			INSERT INTO tmdb_episode(media_id, season_number, episode_number, air_date, runtime, still_path, updated_at)
			VALUES(?,?,?,?,?,?,?)
			ON CONFLICT(media_id, season_number, episode_number) DO UPDATE SET
			  air_date = CASE WHEN excluded.air_date <> '' THEN excluded.air_date ELSE tmdb_episode.air_date END,
			  runtime = CASE WHEN excluded.runtime > 0 THEN excluded.runtime ELSE tmdb_episode.runtime END,
			  still_path = CASE WHEN excluded.still_path <> '' THEN excluded.still_path ELSE tmdb_episode.still_path END,
			  updated_at = excluded.updated_at
		`, mediaRowID, e.SeasonNumber, e.EpisodeNumber, strings.TrimSpace(e.AirDate), e.Runtime, strings.TrimSpace(e.StillPath), now)

		var episodeID int64
		if err := tx.QueryRow(`SELECT id FROM tmdb_episode WHERE media_id=? AND season_number=? AND episode_number=? LIMIT 1`, mediaRowID, e.SeasonNumber, e.EpisodeNumber).Scan(&episodeID); err != nil {
			continue
		}
		if strings.TrimSpace(e.Name) != "" || strings.TrimSpace(e.Overview) != "" {
			_, _ = tx.Exec(`
				INSERT INTO tmdb_episode_i18n(episode_id, lang, name, overview, updated_at)
				VALUES(?,?,?,?,?)
				ON CONFLICT(episode_id, lang) DO UPDATE SET
				  name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE tmdb_episode_i18n.name END,
				  overview = CASE WHEN excluded.overview <> '' THEN excluded.overview ELSE tmdb_episode_i18n.overview END,
				  updated_at = excluded.updated_at
			`, episodeID, l, strings.TrimSpace(e.Name), strings.TrimSpace(e.Overview), now)
		}
	}
	return tx.Commit()
}

func (d *DB) ReadTMDBTVSeasonCounts(tmdbID int) ([]TMDBTVSeasonCountRow, error) {
	if d == nil || d.db == nil {
		return []TMDBTVSeasonCountRow{}, errors.New("db nil")
	}
	if tmdbID <= 0 {
		return []TMDBTVSeasonCountRow{}, errors.New("invalid args")
	}
	rows, err := d.db.Query(`
		SELECT s.season_number, s.episode_count
		FROM tmdb_media m
		JOIN tmdb_season s ON s.media_id = m.id
		WHERE m.tmdb_type = 'tv' AND m.tmdb_id = ?
		ORDER BY s.season_number ASC
	`, tmdbID)
	if err != nil {
		return []TMDBTVSeasonCountRow{}, err
	}
	defer rows.Close()
	out := make([]TMDBTVSeasonCountRow, 0, 8)
	for rows.Next() {
		var row TMDBTVSeasonCountRow
		if err := rows.Scan(&row.SeasonNumber, &row.EpisodeCount); err != nil {
			continue
		}
		if row.SeasonNumber < 0 {
			continue
		}
		out = append(out, row)
	}
	return out, nil
}

func (d *DB) ReadTMDBTVSeasonCountSummary(tmdbID int) (seasonCount int, totalEpisodes int, err error) {
	rows, err := d.ReadTMDBTVSeasonCounts(tmdbID)
	if err != nil {
		return 0, 0, err
	}
	for _, row := range rows {
		seasonCount++
		if row.EpisodeCount > 0 {
			totalEpisodes += row.EpisodeCount
		}
	}
	return seasonCount, totalEpisodes, nil
}

type TMDBCachedDetail struct {
	TMDBID                int
	TMDBType              string
	Adult                 bool
	OriginalLanguage      string
	GenreIDs              []int
	OriginCountry         []string
	Popularity            float64
	VoteAverage           float64
	VoteCount             int
	Title                 string
	Overview              string
	Status                string
	InProduction          bool
	PosterPath            string
	Backdrop              string
	NextEpisodeAirDate    string
	NextEpisodeRefreshDay string
	FirstAir              string
	Release               string
	RuntimeMinutes        int
	Seasons               []TMDBSeason
	EpisodeCount          int
	LatestSeason          int
	LatestEpisode         int
	MetaLevel             string
	SeasonLevel           string
	LastAccessAt          int64
	LastRefreshAt         int64
}

type TMDBCachedSeasonEpisode struct {
	SeasonNumber  int
	EpisodeNumber int
	AirDate       string
	Runtime       int
	StillPath     string
	Name          string
	Overview      string
}

type TMDBCachedSeasonDetail struct {
	TMDBID       int
	Season       int
	Name         string
	Poster       string
	RefreshOnDay string
	LastSyncOKAt int64
	Episodes     []TMDBCachedSeasonEpisode
}

func (d *DB) ReadTMDBLatestSeasonNumber(tmdbID int) (int, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	if tmdbID <= 0 {
		return 0, errors.New("invalid args")
	}
	var latestSeason sql.NullInt64
	if err := d.db.QueryRow(`
		SELECT MAX(s.season_number)
		FROM tmdb_media m
		JOIN tmdb_season s ON s.media_id = m.id
		WHERE m.tmdb_type = 'tv'
		  AND m.tmdb_id = ?
		  AND s.season_number > 0
	`, tmdbID).Scan(&latestSeason); err != nil {
		return 0, err
	}
	if latestSeason.Valid && latestSeason.Int64 > 0 {
		return int(latestSeason.Int64), nil
	}
	return 0, nil
}

func (d *DB) ReadTMDBCachedDetail(tmdbType string, tmdbID int, lang string) (*TMDBCachedDetail, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return nil, errors.New("invalid args")
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}
	var (
		mediaRowID            int64
		adult                 int
		originalLanguage      string
		genreIDsJSON          string
		originCountryJSON     string
		popularity            float64
		voteAverage           float64
		voteCount             int
		posterPath            string
		backdropPath          string
		status                string
		inProduction          int
		nextEpisodeAirDate    string
		nextEpisodeRefreshDay string
		firstAir              string
		releaseDate           string
		runtime               int
		metaLevel             string
		seasonLevel           string
		title                 string
		overview              string
		lastAccessAt          int64
		lastRefreshAt         int64
	)
	err := d.db.QueryRow(`
		SELECT m.id, m.adult, m.original_language, m.genre_ids_json, m.origin_country_json, m.popularity, m.vote_average, m.vote_count,
		       m.poster_path, m.backdrop_path, m.status, m.in_production, m.next_episode_air_date, m.next_episode_refresh_day, m.first_air_date, m.release_date, m.runtime,
		       m.meta_level, m.season_level,
		       m.last_access_at, m.last_refresh_at,
		       COALESCE(i.title,''), COALESCE(i.overview,'')
		FROM tmdb_media m
		LEFT JOIN tmdb_media_i18n i ON i.media_id = m.id AND i.lang = ?
		WHERE m.tmdb_type = ? AND m.tmdb_id = ?
		LIMIT 1
	`, l, typ, tmdbID).Scan(&mediaRowID, &adult, &originalLanguage, &genreIDsJSON, &originCountryJSON, &popularity, &voteAverage, &voteCount, &posterPath, &backdropPath, &status, &inProduction, &nextEpisodeAirDate, &nextEpisodeRefreshDay, &firstAir, &releaseDate, &runtime, &metaLevel, &seasonLevel, &lastAccessAt, &lastRefreshAt, &title, &overview)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	out := &TMDBCachedDetail{
		TMDBID:                tmdbID,
		TMDBType:              typ,
		Adult:                 adult != 0,
		OriginalLanguage:      strings.TrimSpace(originalLanguage),
		GenreIDs:              parseIntJSONArray(genreIDsJSON),
		OriginCountry:         parseStringJSONArray(originCountryJSON),
		Popularity:            popularity,
		VoteAverage:           voteAverage,
		VoteCount:             voteCount,
		Title:                 strings.TrimSpace(title),
		Overview:              strings.TrimSpace(overview),
		Status:                strings.TrimSpace(status),
		InProduction:          inProduction != 0,
		PosterPath:            strings.TrimSpace(posterPath),
		Backdrop:              strings.TrimSpace(backdropPath),
		NextEpisodeAirDate:    strings.TrimSpace(nextEpisodeAirDate),
		NextEpisodeRefreshDay: strings.TrimSpace(nextEpisodeRefreshDay),
		FirstAir:              strings.TrimSpace(firstAir),
		Release:               strings.TrimSpace(releaseDate),
		RuntimeMinutes:        runtime,
		MetaLevel:             normalizeTMDBMetaLevel(metaLevel),
		SeasonLevel:           normalizeTMDBSeasonLevel(seasonLevel),
		LastAccessAt:          lastAccessAt,
		LastRefreshAt:         lastRefreshAt,
	}
	if typ == "movie" {
		out.EpisodeCount = runtime
		return out, nil
	}
	rows, err := d.db.Query(`
		SELECT s.tmdb_season_id, s.season_number, s.episode_count, s.air_date, s.poster_path, COALESCE(si.name,''), COALESCE(si.overview,'')
		FROM tmdb_season s
		LEFT JOIN tmdb_season_i18n si ON si.season_id = s.id AND si.lang = ?
		WHERE s.media_id = ?
		ORDER BY s.season_number ASC
	`, l, mediaRowID)
	if err == nil && rows != nil {
		defer rows.Close()
		for rows.Next() {
			var s TMDBSeason
			_ = rows.Scan(&s.TMDBSeasonID, &s.SeasonNumber, &s.EpisodeCount, &s.AirDate, &s.PosterPath, &s.Name, &s.Overview)
			out.Seasons = append(out.Seasons, s)
		}
	}
	sort.Slice(out.Seasons, func(i, j int) bool { return out.Seasons[i].SeasonNumber < out.Seasons[j].SeasonNumber })

	// Derived stats (no extra storage needed)
	totalFromSeasons := 0
	latestSeason := 0
	latestEpisode := 0
	for _, s := range out.Seasons {
		if s.SeasonNumber <= 0 {
			continue
		}
		if s.EpisodeCount > 0 {
			totalFromSeasons += s.EpisodeCount
		}
	}
	// If episodes table exists, use it for a better latest ep (based on aired air_date).
	// Compare by CN date only (avoid UTC day-shift causing "today" episodes to be excluded).
	loc, err := time.LoadLocation("Asia/Shanghai")
	if err != nil || loc == nil {
		loc = time.FixedZone("CST", 8*3600)
	}
	today := time.Now().In(loc).Format("2006-01-02")
	var ls, le sql.NullInt64
	_ = d.db.QueryRow(`
		SELECT season_number, episode_number
		FROM tmdb_episode
		WHERE media_id = ?
		  AND season_number > 0
		  AND air_date <> ''
		  AND air_date <= ?
		ORDER BY season_number DESC, episode_number DESC
		LIMIT 1
	`, mediaRowID, today).Scan(&ls, &le)
	if ls.Valid && ls.Int64 > 0 && le.Valid && le.Int64 > 0 {
		latestSeason = int(ls.Int64)
		latestEpisode = int(le.Int64)
	} else {
		_ = d.db.QueryRow(`
			SELECT season_number, episode_number
			FROM tmdb_episode
			WHERE media_id = ?
			  AND season_number > 0
			ORDER BY season_number DESC, episode_number DESC
			LIMIT 1
		`, mediaRowID).Scan(&ls, &le)
		if ls.Valid && ls.Int64 > 0 && le.Valid && le.Int64 > 0 {
			latestSeason = int(ls.Int64)
			latestEpisode = int(le.Int64)
		}
	}

	// Prefer episode-derived totals when available (season meta can be stale for airing shows).
	totalFromEpisodes := 0
	if rows, err := d.db.Query(`
		SELECT season_number, MAX(episode_number)
		FROM tmdb_episode
		WHERE media_id = ?
		  AND season_number > 0
		  AND episode_number > 0
		GROUP BY season_number
	`, mediaRowID); err == nil && rows != nil {
		defer rows.Close()
		for rows.Next() {
			var seasonNo sql.NullInt64
			var maxEp sql.NullInt64
			_ = rows.Scan(&seasonNo, &maxEp)
			if maxEp.Valid && maxEp.Int64 > 0 {
				totalFromEpisodes += int(maxEp.Int64)
			}
		}
	}

	// Guard: total should never be lower than the currently known latest aired episode (global minimum).
	minTotalByLatest := 0
	if latestSeason > 0 && latestEpisode > 0 {
		sumPrev := 0
		for _, s := range out.Seasons {
			if s.SeasonNumber <= 0 || s.SeasonNumber >= latestSeason {
				continue
			}
			if s.EpisodeCount > 0 {
				sumPrev += s.EpisodeCount
			}
		}
		minTotalByLatest = sumPrev + latestEpisode
	}

	total := totalFromSeasons
	if totalFromEpisodes > total {
		total = totalFromEpisodes
	}
	if minTotalByLatest > total {
		total = minTotalByLatest
	}

	out.EpisodeCount = total
	out.LatestSeason = latestSeason
	out.LatestEpisode = latestEpisode
	return out, nil
}

func (d *DB) ReadTMDBMediaCompleteness(tmdbType string, tmdbID int, lang string) (*TMDBMediaCompleteness, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	detail, err := d.ReadTMDBCachedDetail(tmdbType, tmdbID, lang)
	if err != nil || detail == nil {
		return nil, err
	}
	out := &TMDBMediaCompleteness{
		MetaLevel:    normalizeTMDBMetaLevel(detail.MetaLevel),
		SeasonLevel:  normalizeTMDBSeasonLevel(detail.SeasonLevel),
		HasOverview:  strings.TrimSpace(detail.Overview) != "",
		SeasonCount:  len(detail.Seasons),
		EpisodeCount: detail.EpisodeCount,
	}
	return out, nil
}

func (d *DB) UpdateTMDBMediaCompleteness(tmdbType string, tmdbID int, metaLevel string, seasonLevel string) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	meta := normalizeTMDBMetaLevel(metaLevel)
	season := normalizeTMDBSeasonLevel(seasonLevel)
	_, err := d.db.Exec(`
		UPDATE tmdb_media
		SET meta_level = CASE
			WHEN ? = 'detail' THEN 'detail'
			WHEN ? = 'search' AND meta_level = 'none' THEN 'search'
			ELSE meta_level
		END,
		season_level = CASE
			WHEN ? = 'episodes' THEN 'episodes'
			WHEN ? = 'summary' AND season_level = 'none' THEN 'summary'
			ELSE season_level
		END,
		updated_at = ?
		WHERE tmdb_type = ? AND tmdb_id = ?
	`, meta, meta, season, season, time.Now().Unix(), typ, tmdbID)
	return err
}

func (d *DB) FindTMDBMediaByTitles(tmdbType string, titles []string, year int, lang string) (int, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if typ != "movie" && typ != "tv" {
		return 0, errors.New("invalid tmdb type")
	}
	candidates := make([]string, 0, len(titles))
	seen := map[string]struct{}{}
	for _, t := range titles {
		s := strings.TrimSpace(t)
		if s == "" {
			continue
		}
		k := strings.ToLower(s)
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		candidates = append(candidates, s)
	}
	if len(candidates) == 0 {
		return 0, nil
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}
	queries := []struct {
		sql  string
		args func(string) []any
	}{}
	yearExpr := "''"
	if typ == "movie" {
		yearExpr = "substr(m.release_date, 1, 4)"
	} else {
		yearExpr = "substr(m.first_air_date, 1, 4)"
	}
	if year > 0 {
		queries = append(queries, struct {
			sql  string
			args func(string) []any
		}{
			sql: `
				SELECT m.tmdb_id
				FROM tmdb_media m
				JOIN tmdb_media_i18n i ON i.media_id = m.id
				WHERE m.tmdb_type = ?
				  AND i.lang = ?
				  AND (lower(trim(i.title)) = lower(trim(?)) OR lower(trim(i.original_title)) = lower(trim(?)))
				  AND ` + yearExpr + ` = ?
				ORDER BY m.updated_at DESC
				LIMIT 1
			`,
			args: func(candidate string) []any { return []any{typ, l, candidate, candidate, strconv.Itoa(year)} },
		})
	}
	queries = append(queries, struct {
		sql  string
		args func(string) []any
	}{
		sql: `
			SELECT m.tmdb_id
			FROM tmdb_media m
			JOIN tmdb_media_i18n i ON i.media_id = m.id
			WHERE m.tmdb_type = ?
			  AND i.lang = ?
			  AND (lower(trim(i.title)) = lower(trim(?)) OR lower(trim(i.original_title)) = lower(trim(?)))
			ORDER BY m.updated_at DESC
			LIMIT 1
		`,
		args: func(candidate string) []any { return []any{typ, l, candidate, candidate} },
	})
	for _, q := range queries {
		for _, candidate := range candidates {
			var tmdbIDFound int
			err := d.db.QueryRow(q.sql, q.args(candidate)...).Scan(&tmdbIDFound)
			if err == nil && tmdbIDFound > 0 {
				return tmdbIDFound, nil
			}
			if err != nil && err != sql.ErrNoRows {
				return 0, err
			}
		}
	}
	return 0, nil
}

func (d *DB) TouchTMDBMediaAccess(tmdbType string, tmdbID int, touchedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	now := touchedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE tmdb_media
		SET last_access_at = ?, updated_at = ?
		WHERE tmdb_type = ? AND tmdb_id = ?
	`, now, now, typ, tmdbID)
	return err
}

func (d *DB) MarkTMDBNextEpisodeRefreshDay(tmdbType string, tmdbID int, day string, refreshedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	stamp := refreshedAt
	if stamp <= 0 {
		stamp = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE tmdb_media
		SET next_episode_refresh_day = ?, last_refresh_at = ?, updated_at = ?
		WHERE tmdb_type = ? AND tmdb_id = ?
	`, strings.TrimSpace(day), stamp, stamp, typ, tmdbID)
	return err
}

func (d *DB) ReadTMDBCachedSeasonDetail(tmdbID int, season int, lang string) (*TMDBCachedSeasonDetail, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	if tmdbID <= 0 || season < 0 {
		return nil, errors.New("invalid args")
	}
	l := strings.TrimSpace(lang)
	if l == "" {
		l = "zh-CN"
	}

	var (
		mediaRowID   int64
		seasonID     int64
		name         string
		poster       string
		refreshOnDay string
		lastSyncOKAt int64
	)
	err := d.db.QueryRow(`
		SELECT m.id, s.id, COALESCE(si.name, ''), s.poster_path, s.refresh_on_day, s.last_sync_ok_at
		FROM tmdb_media m
		JOIN tmdb_season s ON s.media_id = m.id
		LEFT JOIN tmdb_season_i18n si ON si.season_id = s.id AND si.lang = ?
		WHERE m.tmdb_type = 'tv' AND m.tmdb_id = ? AND s.season_number = ?
		LIMIT 1
	`, l, tmdbID, season).Scan(&mediaRowID, &seasonID, &name, &poster, &refreshOnDay, &lastSyncOKAt)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}

	out := &TMDBCachedSeasonDetail{
		TMDBID:       tmdbID,
		Season:       season,
		Name:         strings.TrimSpace(name),
		Poster:       strings.TrimSpace(poster),
		RefreshOnDay: strings.TrimSpace(refreshOnDay),
		LastSyncOKAt: lastSyncOKAt,
	}
	rows, err := d.db.Query(`
		SELECT e.season_number, e.episode_number, e.air_date, e.runtime, e.still_path,
		       COALESCE(i.name, ''), COALESCE(i.overview, '')
		FROM tmdb_episode e
		LEFT JOIN tmdb_episode_i18n i ON i.episode_id = e.id AND i.lang = ?
		WHERE e.media_id = ? AND e.season_number = ?
		ORDER BY e.episode_number ASC
	`, l, mediaRowID, season)
	if err != nil {
		return out, nil
	}
	defer rows.Close()
	for rows.Next() {
		var ep TMDBCachedSeasonEpisode
		_ = rows.Scan(&ep.SeasonNumber, &ep.EpisodeNumber, &ep.AirDate, &ep.Runtime, &ep.StillPath, &ep.Name, &ep.Overview)
		if ep.EpisodeNumber <= 0 {
			continue
		}
		out.Episodes = append(out.Episodes, ep)
	}
	return out, nil
}

func (d *DB) ReadTMDBRawDetailJSON(tmdbType string, tmdbID int) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return "", errors.New("invalid args")
	}
	var raw string
	err := d.db.QueryRow(`
		SELECT detail_json
		FROM tmdb_media
		WHERE tmdb_type = ? AND tmdb_id = ?
		LIMIT 1
	`, typ, tmdbID).Scan(&raw)
	if err != nil {
		if err == sql.ErrNoRows {
			return "", nil
		}
		return "", err
	}
	return strings.TrimSpace(raw), nil
}

func (d *DB) ReadTMDBRawSeasonDetailJSON(tmdbID int, season int) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	if tmdbID <= 0 || season < 0 {
		return "", errors.New("invalid args")
	}
	var raw string
	err := d.db.QueryRow(`
		SELECT s.detail_json
		FROM tmdb_media m
		JOIN tmdb_season s ON s.media_id = m.id
		WHERE m.tmdb_type = 'tv'
		  AND m.tmdb_id = ?
		  AND s.season_number = ?
		LIMIT 1
	`, tmdbID, season).Scan(&raw)
	if err != nil {
		if err == sql.ErrNoRows {
			return "", nil
		}
		return "", err
	}
	return strings.TrimSpace(raw), nil
}

func (d *DB) MarkTMDBSeasonSyncOK(tmdbID int, season int, syncedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if tmdbID <= 0 || season < 0 {
		return errors.New("invalid args")
	}
	stamp := syncedAt
	if stamp <= 0 {
		stamp = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE tmdb_season
		SET last_sync_ok_at = ?, updated_at = ?
		WHERE id IN (
			SELECT s.id
			FROM tmdb_media m
			JOIN tmdb_season s ON s.media_id = m.id
			WHERE m.tmdb_type = 'tv'
			  AND m.tmdb_id = ?
			  AND s.season_number = ?
			LIMIT 1
		)
	`, stamp, stamp, tmdbID, season)
	return err
}

func (d *DB) MarkTMDBSeasonRefreshDay(tmdbID int, season int, day string, syncedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if tmdbID <= 0 || season < 0 {
		return errors.New("invalid args")
	}
	stamp := syncedAt
	if stamp <= 0 {
		stamp = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE tmdb_season
		SET refresh_on_day = ?, last_sync_ok_at = ?, updated_at = ?
		WHERE id IN (
			SELECT s.id
			FROM tmdb_media m
			JOIN tmdb_season s ON s.media_id = m.id
			WHERE m.tmdb_type = 'tv'
			  AND m.tmdb_id = ?
			  AND s.season_number = ?
			LIMIT 1
		)
	`, strings.TrimSpace(day), stamp, stamp, tmdbID, season)
	return err
}
