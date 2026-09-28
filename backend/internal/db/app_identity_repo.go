package db

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"strings"
	"time"
)

func (d *DB) ReadServerIdentity() (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	var raw string
	if err := d.db.QueryRow(`SELECT server_identity FROM app_identity WHERE id=1 LIMIT 1`).Scan(&raw); err != nil {
		return "", err
	}
	return strings.TrimSpace(raw), nil
}

func (d *DB) EnsureServerIdentity() (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	if id, err := d.ReadServerIdentity(); err == nil && id != "" {
		return id, nil
	}
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	id := hex.EncodeToString(b)
	_, err := d.db.Exec(`
		INSERT INTO app_identity(id, server_identity, updated_at)
		VALUES(1, ?, ?)
		ON CONFLICT(id) DO UPDATE SET server_identity=CASE WHEN TRIM(app_identity.server_identity)='' THEN excluded.server_identity ELSE app_identity.server_identity END, updated_at=excluded.updated_at
	`, id, time.Now().Unix())
	if err != nil {
		return "", err
	}
	return d.ReadServerIdentity()
}
