package db

import (
	"database/sql"
	"encoding/json"
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

func (d *DB) ReadTMDBExternalIDs(tmdbType string, tmdbID int) (map[string]string, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	typ := strings.TrimSpace(tmdbType)
	if (typ != "tv" && typ != "movie") || tmdbID <= 0 {
		return nil, errors.New("invalid args")
	}
	rows, err := d.db.Query(`
		SELECT e.source, e.external_id
		FROM tmdb_external_id e
		JOIN tmdb_media m ON m.id = e.media_id
		WHERE m.tmdb_type = ? AND m.tmdb_id = ?
	`, typ, tmdbID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]string{}
	for rows.Next() {
		var source string
		var externalID string
		if err := rows.Scan(&source, &externalID); err != nil {
			return nil, err
		}
		key := normalizeTMDBExternalProviderKey(source)
		if key == "" || strings.TrimSpace(externalID) == "" {
			continue
		}
		out[key] = strings.TrimSpace(externalID)
	}
	return out, rows.Err()
}

func normalizeTMDBExternalProviderKey(raw string) string {
	switch strings.ToLower(strings.TrimSpace(raw)) {
	case "tmdb":
		return "Tmdb"
	case "tvdb":
		return "Tvdb"
	case "imdb":
		return "IMDB"
	case "official website", "official_website", "official-website", "officialwebsite", "homepage", "website":
		return "Official Website"
	case "youtube":
		return "Youtube"
	default:
		return strings.TrimSpace(raw)
	}
}

func (d *DB) ReadTMDBHomepage(tmdbType string, tmdbID int) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	raw, err := d.ReadTMDBRawDetailJSON(tmdbType, tmdbID)
	if err != nil || strings.TrimSpace(raw) == "" {
		return "", err
	}
	var payload struct {
		Homepage string `json:"homepage"`
	}
	if err := json.Unmarshal([]byte(raw), &payload); err != nil {
		return "", err
	}
	return strings.TrimSpace(payload.Homepage), nil
}

func (d *DB) ReadTMDBAverageEpisodeRuntime(tmdbID int) (int, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	if tmdbID <= 0 {
		return 0, errors.New("invalid args")
	}
	var minutes sql.NullFloat64
	err := d.db.QueryRow(`
		SELECT AVG(CAST(e.runtime AS REAL))
		FROM tmdb_episode e
		JOIN tmdb_media m ON m.id = e.media_id
		WHERE m.tmdb_type = 'tv' AND m.tmdb_id = ? AND e.runtime > 0
	`, tmdbID).Scan(&minutes)
	if err != nil {
		return 0, err
	}
	if !minutes.Valid || minutes.Float64 <= 0 {
		return 0, nil
	}
	return int(minutes.Float64 + 0.5), nil
}
