//go:build userlimit

package db

import (
	"crypto/sha1"
	"database/sql"
	"encoding/hex"
	"errors"
	"strings"
)

func normalizeSQLNoWSLower(s string) string {
	if s == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(s))
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == '\f' || c == '\v' {
			continue
		}
		if c >= 'A' && c <= 'Z' {
			c = c - 'A' + 'a'
		}
		b.WriteByte(c)
	}
	return b.String()
}

func sha1Hex(s string) string {
	sum := sha1.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

func verifyUsersLimitTrigger(d *DB) (bool, error) {
	if d == nil || d.db == nil {
		return true, nil
	}
	name := usersLimitTriggerName()
	wantSQL := normalizeSQLNoWSLower(usersLimitTriggerCreateSQL())
	want := sha1Hex(wantSQL)

	var sqlText sql.NullString
	err := d.db.QueryRow(`SELECT sql FROM sqlite_master WHERE type='trigger' AND name=? LIMIT 1`, name).Scan(&sqlText)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return false, nil
		}
		return false, err
	}
	got := sha1Hex(normalizeSQLNoWSLower(sqlText.String))
	return got == want, nil
}

func repairUsersLimitTrigger(d *DB) error {
	if d == nil || d.db == nil {
		return nil
	}
	ddl := strings.TrimSpace(usersLimitTriggerDDL())
	if ddl == "" {
		return nil
	}
	_, err := d.db.Exec(ddl)
	return err
}

func enforceUsersLimitBeforeInsert(d *DB) error {
	if d == nil || d.db == nil {
		return nil
	}
	ok, err := verifyUsersLimitTrigger(d)
	if err != nil {
		return err
	}
	if !ok {
		// Best-effort self-repair: make DB tampering insufficient by recreating the trigger.
		_ = repairUsersLimitTrigger(d)
		ok2, err2 := verifyUsersLimitTrigger(d)
		if err2 != nil {
			return err2
		}
		if !ok2 {
			return errors.New(string([]byte{0x45, 0x31, 0x37}))
		}
	}
	var n int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM users`).Scan(&n); err != nil {
		return err
	}
	if n >= 3 {
		return errors.New(string([]byte{0x45, 0x31, 0x37}))
	}
	return nil
}

func verifyUsersTableShape(d *DB) (bool, error) {
	if d == nil || d.db == nil {
		return true, nil
	}
	rows, err := d.db.Query(`PRAGMA table_info(users)`)
	if err != nil {
		return false, err
	}
	defer rows.Close()

	need := map[string]bool{
		"id":         false,
		"username":   false,
		"password":   false,
		"role":       false,
		"status":     false,
		"created_at": false,
		"updated_at": false,
	}
	for rows.Next() {
		var cid int
		var name, ctype string
		var notnull int
		var dflt sql.NullString
		var pk int
		if err := rows.Scan(&cid, &name, &ctype, &notnull, &dflt, &pk); err != nil {
			return false, err
		}
		if _, ok := need[name]; ok {
			need[name] = true
		}
	}
	for _, ok := range need {
		if !ok {
			return false, nil
		}
	}
	return true, nil
}
