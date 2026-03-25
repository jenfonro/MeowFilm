package db

import (
	"database/sql"
	"errors"
)

var doubanMetadataClearStatements = []string{
	`DELETE FROM douban_detail_cache`,
	`DELETE FROM douban_tmdb_link`,
	`DELETE FROM douban_media_state`,
	`DELETE FROM douban_media_i18n`,
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
		if err := resetTMDBSmartCacheTables(tx); err != nil {
			return err
		}
		return execMetadataStatements(tx, tmdbMetadataClearStatements)
	})
}

func (d *DB) ClearAllMetadataCache() error {
	return d.runMetadataCacheClear(func(tx *sql.Tx) error {
		if err := resetTMDBSmartCacheTables(tx); err != nil {
			return err
		}
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

func resetTMDBSmartCacheTables(tx *sql.Tx) error {
	if _, err := tx.Exec(`DROP TABLE IF EXISTS cache_candidate`); err != nil {
		return err
	}
	if _, err := tx.Exec(`DROP TABLE IF EXISTS cache_episode`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE cache_episode (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  tmdb_media_id INTEGER NOT NULL,
		  season INTEGER NOT NULL DEFAULT 0,
		  episode INTEGER NOT NULL DEFAULT 0,
		  updated_at INTEGER NOT NULL,
		  UNIQUE(tmdb_media_id, season, episode),
		  FOREIGN KEY(tmdb_media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_cache_episode_lookup ON cache_episode(tmdb_media_id, season, episode)`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE cache_candidate (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  episode_id INTEGER NOT NULL,
		  quality_id INTEGER NOT NULL,
		  site_pan_id INTEGER NOT NULL,
		  pan_id INTEGER NOT NULL,
		  rank INTEGER NOT NULL DEFAULT 0,
		  status TEXT NOT NULL DEFAULT 'active',
		  fail_count INTEGER NOT NULL DEFAULT 0,
		  cooldown_until INTEGER NOT NULL DEFAULT 0,
		  last_ok_at INTEGER NOT NULL DEFAULT 0,
		  updated_at INTEGER NOT NULL,
		  UNIQUE(episode_id, quality_id, site_pan_id, pan_id),
		  FOREIGN KEY(episode_id) REFERENCES cache_episode(id) ON DELETE CASCADE,
		  FOREIGN KEY(quality_id) REFERENCES cache_quality(id) ON DELETE CASCADE,
		  FOREIGN KEY(site_pan_id) REFERENCES cache_site_pan(id) ON DELETE CASCADE,
		  FOREIGN KEY(pan_id) REFERENCES cache_pan(id) ON DELETE CASCADE
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_cache_candidate_episode ON cache_candidate(episode_id, updated_at DESC)`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_cache_candidate_cooldown ON cache_candidate(episode_id, cooldown_until)`); err != nil {
		return err
	}
	return nil
}
