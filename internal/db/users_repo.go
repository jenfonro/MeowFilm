package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type UserAuthRow struct {
	ID           int64
	Username     string
	PasswordHash string
	Role         string
	Status       string
	CreatedAt    int64
	UpdatedAt    int64
}

type UserListRow struct {
	Username string
	Role     string
	Status   string
}

func (d *DB) GetUserAuthByUsername(username string) (UserAuthRow, error) {
	if d == nil || d.db == nil {
		return UserAuthRow{}, errors.New("db nil")
	}
	u := strings.TrimSpace(username)
	if u == "" {
		return UserAuthRow{}, sql.ErrNoRows
	}
	var row UserAuthRow
	err := d.db.QueryRow(`SELECT id, username, password, role, status, created_at, updated_at FROM users WHERE username=? LIMIT 1`, u).
		Scan(&row.ID, &row.Username, &row.PasswordHash, &row.Role, &row.Status, &row.CreatedAt, &row.UpdatedAt)
	return row, err
}

func (d *DB) GetUserAuthByID(userID int64) (UserAuthRow, error) {
	if d == nil || d.db == nil {
		return UserAuthRow{}, errors.New("db nil")
	}
	if userID <= 0 {
		return UserAuthRow{}, sql.ErrNoRows
	}
	var row UserAuthRow
	err := d.db.QueryRow(`SELECT id, username, password, role, status, created_at, updated_at FROM users WHERE id=? LIMIT 1`, userID).
		Scan(&row.ID, &row.Username, &row.PasswordHash, &row.Role, &row.Status, &row.CreatedAt, &row.UpdatedAt)
	return row, err
}

func (d *DB) ListUsers() ([]UserListRow, error) {
	if d == nil || d.db == nil {
		return []UserListRow{}, nil
	}
	rows, err := d.db.Query(`
		SELECT u.username, u.role, u.status
		FROM users u
		ORDER BY CASE WHEN u.role = 'admin' THEN 0 ELSE 1 END, u.username
	`)
	if err != nil {
		return []UserListRow{}, err
	}
	defer rows.Close()
	out := []UserListRow{}
	for rows.Next() {
		var r UserListRow
		_ = rows.Scan(&r.Username, &r.Role, &r.Status)
		out = append(out, r)
	}
	return out, nil
}

func (d *DB) GetUserIDByUsername(username string) (int64, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	u := strings.TrimSpace(username)
	if u == "" {
		return 0, sql.ErrNoRows
	}
	var id int64
	err := d.db.QueryRow(`SELECT id FROM users WHERE username=? LIMIT 1`, u).Scan(&id)
	return id, err
}

func (d *DB) CountUsers() (int, error) {
	if d == nil || d.db == nil {
		return 0, nil
	}
	var n int
	err := d.db.QueryRow(`SELECT COUNT(1) FROM users`).Scan(&n)
	return n, err
}

func (d *DB) CreateUser(username string, passwordHash string, role string) (int64, error) {
	if d == nil || d.db == nil {
		return 0, errors.New("db nil")
	}
	if err := d.EnforceUsersLimitBeforeInsert(); err != nil {
		return 0, err
	}
	u := strings.TrimSpace(username)
	if u == "" || strings.TrimSpace(passwordHash) == "" {
		return 0, errors.New("invalid args")
	}
	r := "user"
	if strings.TrimSpace(role) == "admin" {
		r = "admin"
	}
	now := time.Now().Unix()
	res, err := d.db.Exec(`INSERT INTO users(username,password,role,status,created_at,updated_at) VALUES(?,?,?, 'active', ?, ?)`, u, passwordHash, r, now, now)
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (d *DB) UpdateUserUsernameByID(userID int64, newUsername string) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	u := strings.TrimSpace(newUsername)
	if u == "" {
		return errors.New("invalid username")
	}
	_, err := d.db.Exec(`UPDATE users SET username=?, updated_at=? WHERE id=?`, u, time.Now().Unix(), userID)
	return err
}

func (d *DB) UpdateUserPasswordByID(userID int64, passwordHash string) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	if strings.TrimSpace(passwordHash) == "" {
		return errors.New("invalid password hash")
	}
	_, err := d.db.Exec(`UPDATE users SET password=?, updated_at=? WHERE id=?`, passwordHash, time.Now().Unix(), userID)
	return err
}

func (d *DB) UpdateUserRoleByID(userID int64, role string) error {
	if d == nil || d.db == nil || userID <= 0 {
		return nil
	}
	r := strings.TrimSpace(role)
	if r != "user" {
		return errors.New("invalid role")
	}
	_, err := d.db.Exec(`UPDATE users SET role=?, updated_at=? WHERE id=? AND role <> 'admin'`, r, time.Now().Unix(), userID)
	return err
}

func (d *DB) ToggleUserStatusByUsername(username string) (string, error) {
	if d == nil || d.db == nil {
		return "", errors.New("db nil")
	}
	u := strings.TrimSpace(username)
	if u == "" {
		return "", errors.New("invalid username")
	}
	var role, status string
	if err := d.db.QueryRow(`SELECT role, status FROM users WHERE username=? LIMIT 1`, u).Scan(&role, &status); err != nil {
		return "", err
	}
	if strings.TrimSpace(role) == "admin" {
		return "", errors.New("admin immutable")
	}
	next := "active"
	if strings.TrimSpace(status) == "active" {
		next = "banned"
	}
	res, err := d.db.Exec(`UPDATE users SET status=?, updated_at=? WHERE username=? AND role <> 'admin'`, next, time.Now().Unix(), u)
	if err != nil {
		return "", err
	}
	aff, _ := res.RowsAffected()
	if aff <= 0 {
		return "", errors.New("no change")
	}
	return next, nil
}

func (d *DB) DeleteUserByUsername(username string) (deleted bool, err error) {
	if d == nil || d.db == nil {
		return false, errors.New("db nil")
	}
	u := strings.TrimSpace(username)
	if u == "" {
		return false, errors.New("invalid username")
	}
	var id int64
	var role string
	if err := d.db.QueryRow(`SELECT id, role FROM users WHERE username=? LIMIT 1`, u).Scan(&id, &role); err != nil {
		return false, err
	}
	if strings.TrimSpace(role) == "admin" {
		return false, errors.New("admin immutable")
	}

	tx, err := d.db.Begin()
	if err != nil {
		return false, err
	}
	defer func() { _ = tx.Rollback() }()

	_, _ = tx.Exec(`DELETE FROM auth_tokens WHERE user_id=?`, id)
	_, _ = tx.Exec(`DELETE FROM user_search_history WHERE user_id=?`, id)
	_, _ = tx.Exec(`DELETE FROM user_play_history WHERE user_id=?`, id)
	_, _ = tx.Exec(`DELETE FROM user_favorite WHERE user_id=?`, id)

	res, err := tx.Exec(`DELETE FROM users WHERE id=? AND role <> 'admin'`, id)
	if err != nil {
		return false, err
	}
	aff, _ := res.RowsAffected()
	if err := tx.Commit(); err != nil {
		return false, err
	}
	return aff > 0, nil
}

type DeleteUserStats struct {
	TokenDeleted       int64
	HistoryDeleted     int64
	PlayHistoryDeleted int64
	FavoritesDeleted   int64
	UserDeleted        int64
}

func (d *DB) DeleteUserCascadeByUsername(username string) (DeleteUserStats, error) {
	if d == nil || d.db == nil {
		return DeleteUserStats{}, errors.New("db nil")
	}
	u := strings.TrimSpace(username)
	if u == "" {
		return DeleteUserStats{}, errors.New("invalid username")
	}
	var id int64
	var role string
	if err := d.db.QueryRow(`SELECT id, role FROM users WHERE username=? LIMIT 1`, u).Scan(&id, &role); err != nil {
		return DeleteUserStats{}, err
	}
	if strings.TrimSpace(role) == "admin" {
		return DeleteUserStats{}, errors.New("admin immutable")
	}

	tx, err := d.db.Begin()
	if err != nil {
		return DeleteUserStats{}, err
	}
	defer func() { _ = tx.Rollback() }()

	stats := DeleteUserStats{}

	if res, _ := tx.Exec(`DELETE FROM auth_tokens WHERE user_id=?`, id); res != nil {
		stats.TokenDeleted, _ = res.RowsAffected()
	}
	if res, _ := tx.Exec(`DELETE FROM user_search_history WHERE user_id=?`, id); res != nil {
		stats.HistoryDeleted, _ = res.RowsAffected()
	}
	if res, _ := tx.Exec(`DELETE FROM user_play_history WHERE user_id=?`, id); res != nil {
		stats.PlayHistoryDeleted, _ = res.RowsAffected()
	}
	if res, _ := tx.Exec(`DELETE FROM user_favorite WHERE user_id=?`, id); res != nil {
		stats.FavoritesDeleted, _ = res.RowsAffected()
	}
	if res, err := tx.Exec(`DELETE FROM users WHERE id=? AND role <> 'admin'`, id); err != nil {
		return DeleteUserStats{}, err
	} else if res != nil {
		stats.UserDeleted, _ = res.RowsAffected()
	}

	if stats.UserDeleted <= 0 {
		return DeleteUserStats{}, errors.New("no change")
	}
	if err := tx.Commit(); err != nil {
		return DeleteUserStats{}, err
	}
	return stats, nil
}
