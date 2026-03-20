package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type DoubanDetailCacheRow struct {
	Kind          string
	DoubanID      string
	PayloadJSON   string
	LastAccessAt  int64
	LastRefreshAt int64
	UpdatedAt     int64
}

func (d *DB) ReadDoubanDetailCache(kind string, doubanID string) (*DoubanDetailCacheRow, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return nil, errors.New("invalid args")
	}
	var row DoubanDetailCacheRow
	err := d.db.QueryRow(`
		SELECT kind, douban_id, payload_json, last_access_at, last_refresh_at, updated_at
		FROM douban_detail_cache
		WHERE kind = ? AND douban_id = ?
		LIMIT 1
	`, k, id).Scan(&row.Kind, &row.DoubanID, &row.PayloadJSON, &row.LastAccessAt, &row.LastRefreshAt, &row.UpdatedAt)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	row.Kind = strings.TrimSpace(row.Kind)
	row.DoubanID = strings.TrimSpace(row.DoubanID)
	return &row, nil
}

func (d *DB) UpsertDoubanDetailCache(kind string, doubanID string, payloadJSON string, refreshedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	payload := strings.TrimSpace(payloadJSON)
	if k == "" || id == "" || payload == "" {
		return errors.New("invalid args")
	}
	now := refreshedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		INSERT INTO douban_detail_cache(kind, douban_id, payload_json, last_access_at, last_refresh_at, updated_at)
		VALUES(?,?,?,?,?,?)
		ON CONFLICT(kind, douban_id) DO UPDATE SET
		  payload_json = excluded.payload_json,
		  last_access_at = excluded.last_access_at,
		  last_refresh_at = excluded.last_refresh_at,
		  updated_at = excluded.updated_at
	`, k, id, payload, now, now, now)
	return err
}

func (d *DB) TouchDoubanDetailCacheAccess(kind string, doubanID string, touchedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return errors.New("invalid args")
	}
	now := touchedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE douban_detail_cache
		SET last_access_at = ?, updated_at = ?
		WHERE kind = ? AND douban_id = ?
	`, now, now, k, id)
	return err
}
