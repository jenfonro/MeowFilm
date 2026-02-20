package db

import (
	"database/sql"
	"errors"
	"sort"
	"strings"
	"time"
)

type TMDBSeason struct {
	SeasonNumber int
	EpisodeCount int
	AirDate      string
	PosterPath   string
	Name         string
	Overview     string
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
	Type         string // tv|movie
	ID           int
	Lang         string // zh-CN
	Title        string
	Original     string
	Overview     string
	Tagline      string
	Status       string
	PosterPath   string
	BackdropPath string
	FirstAirDate string
	ReleaseDate  string
	Runtime      int
	UpdatedAt    int64
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

	_, _ = tx.Exec(`
		INSERT INTO tmdb_media(tmdb_type, tmdb_id, poster_path, backdrop_path, status, first_air_date, release_date, runtime, updated_at)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(tmdb_type, tmdb_id) DO UPDATE SET
		  poster_path = CASE WHEN excluded.poster_path <> '' THEN excluded.poster_path ELSE tmdb_media.poster_path END,
		  backdrop_path = CASE WHEN excluded.backdrop_path <> '' THEN excluded.backdrop_path ELSE tmdb_media.backdrop_path END,
		  status = CASE WHEN excluded.status <> '' THEN excluded.status ELSE tmdb_media.status END,
		  first_air_date = CASE WHEN excluded.first_air_date <> '' THEN excluded.first_air_date ELSE tmdb_media.first_air_date END,
		  release_date = CASE WHEN excluded.release_date <> '' THEN excluded.release_date ELSE tmdb_media.release_date END,
		  runtime = CASE WHEN excluded.runtime > 0 THEN excluded.runtime ELSE tmdb_media.runtime END,
		  updated_at = excluded.updated_at
	`, typ, m.ID, strings.TrimSpace(m.PosterPath), strings.TrimSpace(m.BackdropPath), strings.TrimSpace(m.Status),
		strings.TrimSpace(m.FirstAirDate), strings.TrimSpace(m.ReleaseDate), m.Runtime, now,
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
			INSERT INTO tmdb_season(media_id, season_number, air_date, poster_path, episode_count, updated_at)
			VALUES(?,?,?,?,?,?)
			ON CONFLICT(media_id, season_number) DO UPDATE SET
			  air_date = CASE WHEN excluded.air_date <> '' THEN excluded.air_date ELSE tmdb_season.air_date END,
			  poster_path = CASE WHEN excluded.poster_path <> '' THEN excluded.poster_path ELSE tmdb_season.poster_path END,
			  episode_count = CASE WHEN excluded.episode_count > 0 THEN excluded.episode_count ELSE tmdb_season.episode_count END,
			  updated_at = excluded.updated_at
		`, mediaRowID, s.SeasonNumber, strings.TrimSpace(s.AirDate), strings.TrimSpace(s.PosterPath), s.EpisodeCount, now)

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

type TMDBDetailForAPI struct {
	TMDBID        int
	TMDBType      string
	Title         string
	Overview      string
	Status        string
	PosterPath    string
	Backdrop      string
	FirstAir      string
	Release       string
	Seasons       []TMDBSeason
	EpisodeCount  int
	LatestSeason  int
	LatestEpisode int
}

func (d *DB) ReadTMDBDetailForAPI(tmdbType string, tmdbID int, lang string) (*TMDBDetailForAPI, error) {
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
		mediaRowID   int64
		posterPath   string
		backdropPath string
		status       string
		firstAir     string
		releaseDate  string
		runtime      int
		title        string
		overview     string
	)
	err := d.db.QueryRow(`
		SELECT m.id, m.poster_path, m.backdrop_path, m.status, m.first_air_date, m.release_date, m.runtime,
		       COALESCE(i.title,''), COALESCE(i.overview,'')
		FROM tmdb_media m
		LEFT JOIN tmdb_media_i18n i ON i.media_id = m.id AND i.lang = ?
		WHERE m.tmdb_type = ? AND m.tmdb_id = ?
		LIMIT 1
	`, l, typ, tmdbID).Scan(&mediaRowID, &posterPath, &backdropPath, &status, &firstAir, &releaseDate, &runtime, &title, &overview)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	out := &TMDBDetailForAPI{
		TMDBID:     tmdbID,
		TMDBType:   typ,
		Title:      strings.TrimSpace(title),
		Overview:   strings.TrimSpace(overview),
		Status:     strings.TrimSpace(status),
		PosterPath: strings.TrimSpace(posterPath),
		Backdrop:   strings.TrimSpace(backdropPath),
		FirstAir:   strings.TrimSpace(firstAir),
		Release:    strings.TrimSpace(releaseDate),
	}
	if typ == "movie" {
		out.EpisodeCount = runtime
		return out, nil
	}
	rows, err := d.db.Query(`
		SELECT s.season_number, s.episode_count, s.air_date, s.poster_path, COALESCE(si.name,''), COALESCE(si.overview,'')
		FROM tmdb_season s
		LEFT JOIN tmdb_season_i18n si ON si.season_id = s.id AND si.lang = ?
		WHERE s.media_id = ?
		ORDER BY s.season_number ASC
	`, l, mediaRowID)
	if err == nil && rows != nil {
		defer rows.Close()
		for rows.Next() {
			var s TMDBSeason
			_ = rows.Scan(&s.SeasonNumber, &s.EpisodeCount, &s.AirDate, &s.PosterPath, &s.Name, &s.Overview)
			out.Seasons = append(out.Seasons, s)
		}
	}
	sort.Slice(out.Seasons, func(i, j int) bool { return out.Seasons[i].SeasonNumber < out.Seasons[j].SeasonNumber })

	// Derived stats (no extra storage needed)
	total := 0
	latestSeason := 0
	latestEpisode := 0
	for _, s := range out.Seasons {
		if s.SeasonNumber <= 0 {
			continue
		}
		if s.EpisodeCount > 0 {
			total += s.EpisodeCount
		}
	}
	// If episodes table exists, use it for a better latest ep (based on aired air_date).
	today := time.Now().UTC().Format("2006-01-02")
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
	out.EpisodeCount = total
	out.LatestSeason = latestSeason
	out.LatestEpisode = latestEpisode
	return out, nil
}
