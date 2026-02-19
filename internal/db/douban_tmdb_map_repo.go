package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type DoubanTMDBMap struct {
	Kind       string
	DoubanID   string
	Title      string
	Year       int
	TMDBID     int
	TMDBKind   string
	LastTryAt  int64
	LastTryKey string
	UpdatedAt  int64
}

func (d *DB) GetDoubanTMDBMap(kind string, doubanID string) (*DoubanTMDBMap, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return nil, errors.New("invalid args")
	}
	lang := "zh-CN"
	var row DoubanTMDBMap
	var tmdbMediaID sql.NullInt64
	err := d.db.QueryRow(`
		SELECT
		  m.kind,
		  m.douban_id,
		  COALESCE(i.title,'') AS title,
		  m.year,
		  l.tmdb_media_id,
		  COALESCE(s.last_try_at, 0) AS last_try_at,
		  COALESCE(s.last_try_key, '') AS last_try_key,
		  m.updated_at
		FROM douban_media m
		LEFT JOIN douban_media_i18n i ON i.media_id = m.id AND i.lang = ?
		LEFT JOIN douban_tmdb_link l ON l.douban_media_id = m.id
		LEFT JOIN douban_media_state s ON s.media_id = m.id
		WHERE m.kind=? AND m.douban_id=?
		LIMIT 1
	`, lang, k, id).Scan(&row.Kind, &row.DoubanID, &row.Title, &row.Year, &tmdbMediaID, &row.LastTryAt, &row.LastTryKey, &row.UpdatedAt)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	if tmdbMediaID.Valid && tmdbMediaID.Int64 > 0 {
		var tid int
		var ttyp string
		_ = d.db.QueryRow(`SELECT tmdb_id, tmdb_type FROM tmdb_media WHERE id=? LIMIT 1`, tmdbMediaID.Int64).Scan(&tid, &ttyp)
		row.TMDBID = tid
		row.TMDBKind = strings.TrimSpace(ttyp)
	}
	row.Kind = strings.TrimSpace(row.Kind)
	row.DoubanID = strings.TrimSpace(row.DoubanID)
	row.Title = strings.TrimSpace(row.Title)
	row.LastTryKey = strings.TrimSpace(row.LastTryKey)
	row.TMDBKind = strings.TrimSpace(row.TMDBKind)
	return &row, nil
}

func (d *DB) UpsertDoubanTMDBMap(m DoubanTMDBMap) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	k := strings.TrimSpace(m.Kind)
	id := strings.TrimSpace(m.DoubanID)
	if k == "" || id == "" {
		return errors.New("invalid args")
	}
	now := time.Now().Unix()
	lang := "zh-CN"
	title := strings.TrimSpace(m.Title)
	year := m.Year
	lastTryAt := m.LastTryAt
	if lastTryAt < 0 {
		lastTryAt = 0
	}
	lastTryKey := strings.TrimSpace(m.LastTryKey)

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	_, _ = tx.Exec(`
		INSERT INTO douban_media(kind, douban_id, year, created_at, updated_at)
		VALUES(?,?,?,?,?)
		ON CONFLICT(kind, douban_id) DO UPDATE SET
		  year = CASE WHEN excluded.year > 0 THEN excluded.year ELSE douban_media.year END,
		  updated_at = excluded.updated_at
	`, k, id, year, now, now)

	var doubanMediaID int64
	if err := tx.QueryRow(`SELECT id FROM douban_media WHERE kind=? AND douban_id=? LIMIT 1`, k, id).Scan(&doubanMediaID); err != nil {
		return err
	}

	if title != "" {
		_, _ = tx.Exec(`
			INSERT INTO douban_media_i18n(media_id, lang, title, updated_at)
			VALUES(?,?,?,?)
			ON CONFLICT(media_id, lang) DO UPDATE SET
			  title = CASE WHEN excluded.title <> '' THEN excluded.title ELSE douban_media_i18n.title END,
			  updated_at = excluded.updated_at
		`, doubanMediaID, lang, title, now)
	}

	if lastTryAt > 0 || lastTryKey != "" {
		_, _ = tx.Exec(`
			INSERT INTO douban_media_state(media_id, last_try_at, last_try_key, updated_at)
			VALUES(?,?,?,?)
			ON CONFLICT(media_id) DO UPDATE SET
			  last_try_at = CASE WHEN excluded.last_try_at > 0 THEN excluded.last_try_at ELSE douban_media_state.last_try_at END,
			  last_try_key = CASE WHEN excluded.last_try_key <> '' THEN excluded.last_try_key ELSE douban_media_state.last_try_key END,
			  updated_at = excluded.updated_at
		`, doubanMediaID, lastTryAt, lastTryKey, now)
	}

	// Link to TMDB media when provided.
	if m.TMDBID > 0 && (strings.TrimSpace(m.TMDBKind) == "tv" || strings.TrimSpace(m.TMDBKind) == "movie") {
		typ := strings.TrimSpace(m.TMDBKind)
		// Ensure tmdb_media exists.
		_, _ = tx.Exec(`INSERT INTO tmdb_media(tmdb_type, tmdb_id, updated_at) VALUES(?,?,?) ON CONFLICT(tmdb_type, tmdb_id) DO UPDATE SET updated_at=excluded.updated_at`, typ, m.TMDBID, now)
		var tmdbMediaID int64
		if err := tx.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type=? AND tmdb_id=? LIMIT 1`, typ, m.TMDBID).Scan(&tmdbMediaID); err == nil && tmdbMediaID > 0 {
			_, _ = tx.Exec(`
				INSERT INTO douban_tmdb_link(douban_media_id, tmdb_media_id, updated_at)
				VALUES(?,?,?)
				ON CONFLICT(douban_media_id) DO UPDATE SET tmdb_media_id=excluded.tmdb_media_id, updated_at=excluded.updated_at
			`, doubanMediaID, tmdbMediaID, now)
		}
	}

	return tx.Commit()
}

