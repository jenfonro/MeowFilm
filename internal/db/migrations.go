package db

import (
	"database/sql"
	"errors"
	"strings"
)

type DBMigration struct {
	From  string
	To    string
	Apply func(tx *sql.Tx) error
}

var dbMigrations = []DBMigration{
	{
		From:  "1.1.0",
		To:    "1.1.1",
		Apply: migrate_1_1_0_to_1_1_1,
	},
}

func findMigrationFrom(version string) (DBMigration, bool) {
	current := strings.TrimSpace(version)
	for _, migration := range dbMigrations {
		if strings.TrimSpace(migration.From) == current {
			return migration, true
		}
	}
	return DBMigration{}, false
}

func (d *DB) getSchemaVersion() (string, bool, error) {
	if d == nil || d.db == nil {
		return "", false, nil
	}
	var version sql.NullString
	err := d.db.QueryRow(`SELECT value FROM db_meta WHERE key='schema_version' LIMIT 1`).Scan(&version)
	if errors.Is(err, sql.ErrNoRows) {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	return strings.TrimSpace(version.String), true, nil
}

func (d *DB) setSchemaVersion(version string) error {
	if d == nil || d.db == nil {
		return nil
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	if err := d.setSchemaVersionTx(tx, version); err != nil {
		_ = tx.Rollback()
		return err
	}
	return tx.Commit()
}

func (d *DB) setSchemaVersionTx(tx *sql.Tx, version string) error {
	if tx == nil {
		return nil
	}
	_, err := tx.Exec(`
		INSERT INTO db_meta(key, value)
		VALUES('schema_version', ?)
		ON CONFLICT(key) DO UPDATE SET value=excluded.value
	`, strings.TrimSpace(version))
	return err
}

func migrate_1_1_0_to_1_1_1(tx *sql.Tx) error {
	hasRulesColumn, err := hasColumnTx(tx, "app_smart", "source_priority_rules_json")
	if err != nil {
		return err
	}
	if !hasRulesColumn {
		if _, err := tx.Exec(`ALTER TABLE app_smart ADD COLUMN source_priority_rules_json TEXT NOT NULL DEFAULT '[]'`); err != nil {
			return err
		}
	}

	var priority, rulesRaw sql.NullString
	err = tx.QueryRow(`SELECT source_extract_priority, source_priority_rules_json FROM app_smart WHERE id=1 LIMIT 1`).Scan(&priority, &rulesRaw)
	if errors.Is(err, sql.ErrNoRows) {
		return nil
	}
	if err != nil {
		return err
	}
	if len(parseSmartSourceRuleRowsJSON(rulesRaw.String)) > 0 {
		return nil
	}
	rules := buildSmartSourceRuleRowsFromLegacyMode(priority.String)
	_, err = tx.Exec(`UPDATE app_smart SET source_priority_rules_json=? WHERE id=1`, marshalSmartSourceRuleRowsJSON(rules))
	return err
}

func hasColumnTx(tx *sql.Tx, table, column string) (bool, error) {
	if tx == nil {
		return false, nil
	}
	rows, err := tx.Query(`PRAGMA table_info(` + table + `)`)
	if err != nil {
		return false, err
	}
	defer rows.Close()
	for rows.Next() {
		var (
			cid      int
			name     string
			colType  string
			notNull  int
			defaultV sql.NullString
			pk       int
		)
		if err := rows.Scan(&cid, &name, &colType, &notNull, &defaultV, &pk); err != nil {
			return false, err
		}
		if strings.EqualFold(strings.TrimSpace(name), strings.TrimSpace(column)) {
			return true, nil
		}
	}
	return false, rows.Err()
}
