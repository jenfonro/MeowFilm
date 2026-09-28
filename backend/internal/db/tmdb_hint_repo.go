package db

import (
	"database/sql"
	"errors"
	"sort"
	"strings"
	"time"
)

type TMDBSeasonHint struct {
	SeasonNumber int
	EpisodeCount int
}

func (d *DB) UpsertTMDBSeasonHints(tmdbType string, tmdbID int, source string, hints []TMDBSeasonHint) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if typ != "tv" || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	src := strings.TrimSpace(strings.ToLower(source))
	if src == "" {
		return errors.New("invalid source")
	}
	if len(hints) == 0 {
		return nil
	}
	var mediaRowID int64
	if err := d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type='tv' AND tmdb_id=? LIMIT 1`, tmdbID).Scan(&mediaRowID); err != nil {
		if err == sql.ErrNoRows {
			// create a minimal placeholder media row (so hints can be stored)
			now := time.Now().Unix()
			_, _ = d.db.Exec(`INSERT INTO tmdb_media(tmdb_type, tmdb_id, updated_at) VALUES('tv', ?, ?)`, tmdbID, now)
			_ = d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type='tv' AND tmdb_id=? LIMIT 1`, tmdbID).Scan(&mediaRowID)
		} else {
			return err
		}
	}
	if mediaRowID <= 0 {
		return nil
	}
	now := time.Now().Unix()
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	for _, h := range hints {
		if h.SeasonNumber <= 0 || h.EpisodeCount <= 0 {
			continue
		}
		_, _ = tx.Exec(`
			INSERT INTO tmdb_season_hint(media_id, source, season_number, episode_count, updated_at)
			VALUES(?,?,?,?,?)
			ON CONFLICT(media_id, source, season_number) DO UPDATE SET
			  episode_count = CASE WHEN excluded.episode_count > 0 THEN excluded.episode_count ELSE tmdb_season_hint.episode_count END,
			  updated_at = excluded.updated_at
		`, mediaRowID, src, h.SeasonNumber, h.EpisodeCount, now)
	}
	return tx.Commit()
}

func (d *DB) ListTMDBSeasonHints(tmdbType string, tmdbID int, source string) ([]TMDBSeasonHint, error) {
	if d == nil || d.db == nil {
		return []TMDBSeasonHint{}, nil
	}
	typ := strings.TrimSpace(tmdbType)
	if typ != "tv" || tmdbID <= 0 {
		return []TMDBSeasonHint{}, nil
	}
	src := strings.TrimSpace(strings.ToLower(source))
	if src == "" {
		return []TMDBSeasonHint{}, nil
	}
	var mediaRowID int64
	if err := d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type='tv' AND tmdb_id=? LIMIT 1`, tmdbID).Scan(&mediaRowID); err != nil {
		return []TMDBSeasonHint{}, nil
	}
	rows, err := d.db.Query(`
		SELECT season_number, episode_count
		FROM tmdb_season_hint
		WHERE media_id = ? AND source = ?
		ORDER BY season_number ASC
	`, mediaRowID, src)
	if err != nil {
		return []TMDBSeasonHint{}, err
	}
	defer rows.Close()
	out := []TMDBSeasonHint{}
	for rows.Next() {
		var h TMDBSeasonHint
		_ = rows.Scan(&h.SeasonNumber, &h.EpisodeCount)
		if h.SeasonNumber > 0 && h.EpisodeCount > 0 {
			out = append(out, h)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].SeasonNumber < out[j].SeasonNumber })
	return out, nil
}
