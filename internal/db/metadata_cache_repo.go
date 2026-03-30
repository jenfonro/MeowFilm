package db

import (
	"database/sql"
	"errors"
)

var doubanMetadataClearStatements = []string{
	`DELETE FROM douban_detail_cache`,
	`DELETE FROM douban_tmdb_link`,
	`DELETE FROM douban_media`,
}

var tmdbMetadataClearStatements = []string{
	`DELETE FROM douban_tmdb_link`,
	`DELETE FROM tmdb_external_id`,
	`DELETE FROM tmdb_season_hint`,
	`DELETE FROM tmdb_episode_i18n`,
	`DELETE FROM tmdb_episode`,
	`DELETE FROM tmdb_season_i18n`,
	`DELETE FROM tmdb_season`,
	`DELETE FROM tmdb_media_i18n`,
	`DELETE FROM tmdb_media`,
}

func (d *DB) ClearDoubanMetadataCache() error {
	return d.runMetadataCacheClear(func(tx *sql.Tx) error {
		return execMetadataStatements(tx, doubanMetadataClearStatements)
	})
}

func (d *DB) ClearTMDBMetadataCache() error {
	return d.runMetadataCacheClear(func(tx *sql.Tx) error {
		return execMetadataStatements(tx, tmdbMetadataClearStatements)
	})
}

func (d *DB) ClearAllMetadataCache() error {
	return d.runMetadataCacheClear(func(tx *sql.Tx) error {
		if err := execMetadataStatements(tx, doubanMetadataClearStatements); err != nil {
			return err
		}
		return execMetadataStatements(tx, tmdbMetadataClearStatements)
	})
}

func (d *DB) runMetadataCacheClear(run func(tx *sql.Tx) error) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if err := run(tx); err != nil {
		return err
	}
	return tx.Commit()
}

func execMetadataStatements(tx *sql.Tx, statements []string) error {
	for _, stmt := range statements {
		if _, err := tx.Exec(stmt); err != nil {
			return err
		}
	}
	return nil
}
