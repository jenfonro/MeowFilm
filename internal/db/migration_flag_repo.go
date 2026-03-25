package db

import (
	"errors"
	"time"
)

func (d *DB) HasMigrationFlag(name string) (bool, error) {
	if d == nil || d.db == nil {
		return false, errors.New("db nil")
	}
	if name == "" {
		return false, nil
	}
	var done int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM app_migration_flag WHERE name=?`, name).Scan(&done); err != nil {
		return false, err
	}
	return done > 0, nil
}

func (d *DB) MarkMigrationFlag(name string) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if name == "" {
		return nil
	}
	_, err := d.db.Exec(`INSERT INTO app_migration_flag(name, updated_at) VALUES(?, ?)`, name, time.Now().Unix())
	return err
}
