package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

func (d *DB) UpsertTMDBExternalID(tmdbType string, tmdbID int, source string, externalID string) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	src := strings.TrimSpace(strings.ToLower(source))
	ext := strings.TrimSpace(externalID)
	if src == "" || ext == "" {
		return errors.New("invalid external id")
	}
	var mediaRowID int64
	if err := d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type=? AND tmdb_id=? LIMIT 1`, typ, tmdbID).Scan(&mediaRowID); err != nil {
		if err == sql.ErrNoRows {
			// create minimal placeholder
			now := time.Now().Unix()
			_, _ = d.db.Exec(`INSERT INTO tmdb_media(tmdb_type, tmdb_id, updated_at) VALUES(?,?,?)`, typ, tmdbID, now)
			_ = d.db.QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type=? AND tmdb_id=? LIMIT 1`, typ, tmdbID).Scan(&mediaRowID)
		} else {
			return err
		}
	}
	if mediaRowID <= 0 {
		return nil
	}
	_, err := d.db.Exec(`
		INSERT INTO tmdb_external_id(media_id, source, external_id, updated_at)
		VALUES(?,?,?,?)
		ON CONFLICT(media_id, source) DO UPDATE SET
		  external_id = excluded.external_id,
		  updated_at = excluded.updated_at
	`, mediaRowID, src, ext, time.Now().Unix())
	return err
}

