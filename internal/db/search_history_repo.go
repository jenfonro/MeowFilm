package db

import (
	"database/sql"
	"strings"
	"time"
)

func (d *DB) ListSearchHistory(userID int64, limit int) ([]string, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return []string{}, nil
	}
	if limit <= 0 {
		limit = 20
	}
	if limit > 200 {
		limit = 200
	}
	rows, err := d.db.Query(`
		SELECT k.keyword
		FROM user_search_history h
		JOIN search_keyword k ON k.id = h.keyword_id
		WHERE h.user_id = ?
		ORDER BY h.updated_at DESC
		LIMIT ?
	`, userID, limit)
	if err != nil {
		return []string{}, err
	}
	defer rows.Close()
	out := []string{}
	for rows.Next() {
		var kw sql.NullString
		_ = rows.Scan(&kw)
		s := strings.TrimSpace(kw.String)
		if s != "" {
			out = append(out, s)
		}
	}
	return out, nil
}

func (d *DB) UpsertSearchHistory(userID int64, keyword string) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	kw := strings.Join(strings.Fields(strings.TrimSpace(keyword)), " ")
	if kw == "" {
		return nil
	}
	now := time.Now().Unix()
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	_, _ = tx.Exec(`
		INSERT INTO search_keyword(keyword, created_at, updated_at)
		VALUES(?,?,?)
		ON CONFLICT(keyword) DO UPDATE SET updated_at = excluded.updated_at
	`, kw, now, now)

	var kid int64
	if err := tx.QueryRow(`SELECT id FROM search_keyword WHERE keyword = ? LIMIT 1`, kw).Scan(&kid); err != nil {
		return err
	}
	_, err = tx.Exec(`
		INSERT INTO user_search_history(user_id, keyword_id, updated_at)
		VALUES(?,?,?)
		ON CONFLICT(user_id, keyword_id) DO UPDATE SET updated_at = excluded.updated_at
	`, userID, kid, now)
	if err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) DeleteSearchHistoryKeyword(userID int64, keyword string) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	kw := strings.TrimSpace(keyword)
	if kw == "" {
		return nil
	}
	var kid int64
	if err := d.db.QueryRow(`SELECT id FROM search_keyword WHERE keyword = ? LIMIT 1`, kw).Scan(&kid); err != nil {
		return nil
	}
	_, err := d.db.Exec(`DELETE FROM user_search_history WHERE user_id=? AND keyword_id=?`, userID, kid)
	return err
}

func (d *DB) ClearSearchHistory(userID int64) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	_, err := d.db.Exec(`DELETE FROM user_search_history WHERE user_id=?`, userID)
	return err
}

