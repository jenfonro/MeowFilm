package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type TokenUserRow struct {
	UserID    int64
	Username  string
	Role      string
	Status    string
	ExpiresAt time.Time
}

func (d *DB) ResolveToken(token string) (*TokenUserRow, error) {
	if d == nil || d.db == nil {
		return nil, errors.New("db nil")
	}
	tok := strings.TrimSpace(token)
	if tok == "" {
		return nil, sql.ErrNoRows
	}
	var (
		row   TokenUserRow
		expMS int64
	)
	err := d.db.QueryRow(`
		SELECT t.user_id, u.username, u.role, u.status, t.expires_at
		FROM auth_tokens t
		JOIN users u ON u.id = t.user_id
		WHERE t.token = ?
		LIMIT 1
	`, tok).Scan(&row.UserID, &row.Username, &row.Role, &row.Status, &expMS)
	if err != nil {
		return nil, err
	}
	row.ExpiresAt = time.UnixMilli(expMS)
	row.Username = strings.TrimSpace(row.Username)
	row.Role = strings.TrimSpace(row.Role)
	row.Status = strings.TrimSpace(row.Status)
	return &row, nil
}

func (d *DB) InsertToken(token string, userID int64, expiresAt time.Time) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	tok := strings.TrimSpace(token)
	if tok == "" || userID <= 0 {
		return errors.New("invalid args")
	}
	now := time.Now()
	_, err := d.db.Exec(`INSERT INTO auth_tokens(token, user_id, created_at, expires_at) VALUES (?,?,?,?)`,
		tok, userID, now.UnixMilli(), expiresAt.UnixMilli(),
	)
	return err
}

func (d *DB) DeleteToken(token string) error {
	if d == nil || d.db == nil {
		return nil
	}
	tok := strings.TrimSpace(token)
	if tok == "" {
		return nil
	}
	_, err := d.db.Exec(`DELETE FROM auth_tokens WHERE token = ?`, tok)
	return err
}
