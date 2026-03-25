package db

import (
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"strings"
	"time"
)

type UserProtocolIdentityRow struct {
	UserID     int64
	Protocol   string
	ExternalID string
	UpdatedAt  int64
}

func (d *DB) GetUserProtocolIdentity(userID int64, protocol string) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	if userID <= 0 {
		return "", errors.New("invalid user id")
	}
	p := strings.ToLower(strings.TrimSpace(protocol))
	if p == "" {
		return "", errors.New("invalid protocol")
	}
	var raw string
	err := d.db.QueryRow(`SELECT external_id FROM user_protocol_identity WHERE user_id=? AND protocol=? LIMIT 1`, userID, p).Scan(&raw)
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(raw), nil
}

func (d *DB) ResolveUserProtocolIdentity(protocol string, externalID string) (int64, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	p := strings.ToLower(strings.TrimSpace(protocol))
	eid := strings.TrimSpace(externalID)
	if p == "" || eid == "" {
		return 0, sql.ErrNoRows
	}
	var userID int64
	err := d.db.QueryRow(`SELECT user_id FROM user_protocol_identity WHERE protocol=? AND external_id=? LIMIT 1`, p, eid).Scan(&userID)
	if err != nil {
		return 0, err
	}
	return userID, nil
}

func (d *DB) GetOrCreateUserProtocolIdentity(userID int64, protocol string) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	if userID <= 0 {
		return "", errors.New("invalid user id")
	}
	p := strings.ToLower(strings.TrimSpace(protocol))
	if p == "" {
		return "", errors.New("invalid protocol")
	}
	var raw string
	err := d.db.QueryRow(`SELECT external_id FROM user_protocol_identity WHERE user_id=? AND protocol=? LIMIT 1`, userID, p).Scan(&raw)
	if err == nil && strings.TrimSpace(raw) != "" {
		return strings.TrimSpace(raw), nil
	}
	if err != nil && !errors.Is(err, ErrNoRowsCompat()) {
		// fall through for sql no rows only
		if !strings.Contains(strings.ToLower(err.Error()), "no rows") {
			return "", err
		}
	}

	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	externalID := hex.EncodeToString(b)
	_, err = d.db.Exec(`
		INSERT INTO user_protocol_identity(user_id, protocol, external_id, updated_at)
		VALUES(?, ?, ?, ?)
		ON CONFLICT(user_id, protocol) DO UPDATE SET updated_at=excluded.updated_at
	`, userID, p, externalID, time.Now().Unix())
	if err != nil {
		return "", err
	}
	return externalID, nil
}

func ErrNoRowsCompat() error { return errors.New("sql: no rows in result set") }
