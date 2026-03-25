package db

import "errors"

func (d *DB) ClearDoubanMetadataCache() error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if _, err := tx.Exec(`DELETE FROM douban_detail_cache`); err != nil {
		return err
	}
	if _, err := tx.Exec(`DELETE FROM douban_media`); err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) ClearTMDBMetadataCache() error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if _, err := tx.Exec(`DELETE FROM tmdb_media`); err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) ClearAllMetadataCache() error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if _, err := tx.Exec(`DELETE FROM douban_detail_cache`); err != nil {
		return err
	}
	if _, err := tx.Exec(`DELETE FROM douban_media`); err != nil {
		return err
	}
	if _, err := tx.Exec(`DELETE FROM tmdb_media`); err != nil {
		return err
	}
	return tx.Commit()
}
