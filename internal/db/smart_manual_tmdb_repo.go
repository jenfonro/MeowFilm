package db

import (
	"errors"
	"strings"
	"time"
)

type SmartManualTMDBItem struct {
	TMDBType  string
	TMDBID    int
	Title     string
	Count     int
	UpdatedAt int64
}

func normalizeSmartManualTMDBType(value string) string {
	typ := strings.TrimSpace(strings.ToLower(value))
	if typ == "tv" || typ == "movie" {
		return typ
	}
	return ""
}

func (d *DB) ListSmartManualTMDBItems() ([]SmartManualTMDBItem, error) {
	if d == nil || d.db == nil {
		return []SmartManualTMDBItem{}, nil
	}
	rows, err := d.db.Query(`
		SELECT t.tmdb_type, t.tmdb_id, t.title, t.updated_at,
		  COALESCE((
		    SELECT COUNT(1)
		    FROM smart_manual_item i
		    WHERE i.tmdb_type=t.tmdb_type AND i.tmdb_id=t.tmdb_id
		  ), 0) AS item_count
		FROM smart_manual_tmdb t
		ORDER BY updated_at DESC, tmdb_type ASC, tmdb_id DESC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := make([]SmartManualTMDBItem, 0, 32)
	for rows.Next() {
		var item SmartManualTMDBItem
		_ = rows.Scan(&item.TMDBType, &item.TMDBID, &item.Title, &item.UpdatedAt, &item.Count)
		item.TMDBType = normalizeSmartManualTMDBType(item.TMDBType)
		item.Title = strings.TrimSpace(item.Title)
		if item.Count < 0 {
			item.Count = 0
		}
		if item.TMDBType == "" || item.TMDBID <= 0 || item.Title == "" {
			continue
		}
		out = append(out, item)
	}
	return out, nil
}

func (d *DB) UpsertSmartManualTMDBItem(tmdbType string, tmdbID int, title string) error {
	if d == nil || d.db == nil {
		return nil
	}
	typ := normalizeSmartManualTMDBType(tmdbType)
	id := tmdbID
	t := strings.TrimSpace(title)
	if typ == "" || id <= 0 || t == "" {
		return errors.New("invalid params")
	}
	now := time.Now().Unix()
	_, err := d.db.Exec(`
		INSERT INTO smart_manual_tmdb(tmdb_type, tmdb_id, title, created_at, updated_at)
		VALUES(?, ?, ?, ?, ?)
		ON CONFLICT(tmdb_type, tmdb_id) DO UPDATE SET
		  title=excluded.title,
		  updated_at=excluded.updated_at
	`, typ, id, t, now, now)
	return err
}

func (d *DB) DeleteSmartManualTMDBItem(tmdbType string, tmdbID int) error {
	if d == nil || d.db == nil {
		return nil
	}
	typ := normalizeSmartManualTMDBType(tmdbType)
	id := tmdbID
	if typ == "" || id <= 0 {
		return errors.New("invalid params")
	}
	_, err := d.db.Exec(`DELETE FROM smart_manual_tmdb WHERE tmdb_type=? AND tmdb_id=?`, typ, id)
	return err
}
