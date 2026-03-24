package db

import (
	"database/sql"
	"strings"
	"time"
)

func (d *DB) ensureDefaults(_ bool) error {
	if d == nil || d.db == nil {
		return nil
	}

	now := time.Now().Unix()

	// Ensure per-domain app config rows exist (id=1).
	_, _ = d.db.Exec(`INSERT INTO app_site(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_douban(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_tmdb(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_video_source(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_search(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_smart(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_goproxy(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_relay(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_netdisk_proxy(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_catpawrunner(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)
	_, _ = d.db.Exec(`INSERT INTO app_emby(id, updated_at) VALUES(1, ?) ON CONFLICT(id) DO NOTHING`, now)

	type seedTable struct {
		Table string
		Seed  func(tx *sql.Tx) error
	}
	seeds := []seedTable{
		{
			Table: "magic_episode_rule",
			Seed: func(tx *sql.Tx) error {
				// Keep the legacy front-end compatible payload shape: a JSON array of JSON-string rules.
				// Defaults aligned with MeowFilm-Frontend "restore defaults".
				rules := []string{
					`{"pattern":".*?([Ss]\\\\d{1,2})?(?:第\\\\s*(\\\\d{1,4})\\\\s*(?:集|话)|[Ee][Pp]?\\\\s*(\\\\d{1,4})(?:$|\\\\D)).*?.*","replace":"$1E$2$3","flags":"i"}`,
					`{"pattern":"^[\\\\s\\\\[\\\\]\\\\(\\\\){}【】._-]*0*(\\\\d{1,4})[\\\\s\\\\[\\\\]\\\\(\\\\){}【】._-]*(?:\\\\.[A-Za-z0-9]{1,6})?\\\\s*$","replace":"E$1","flags":"i"}`,
				}
				for i, r := range rules {
					if _, err := tx.Exec(`INSERT INTO magic_episode_rule(pos, rule_text) VALUES(?, ?)`, i, r); err != nil {
						return err
					}
				}
				return nil
			},
		},
		{
			Table: "magic_episode_clean_regex_rule",
			Seed: func(tx *sql.Tx) error {
				// Defaults aligned with MeowFilm-Frontend "restore defaults".
				rules := []string{
					`\[(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\])[^\]]*\]`,
					`【(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*】)[^】]*】`,
					`\((?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\))[^)]*\)`,
					`（(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*）)[^）]*）`,
					`(?:^|[\s\[\]\(\){}【】._-])(?:4k|8k|2160p|1080p|720p)(?=$|[\s\[\]\(\){}【】._-])`,
					`高\s*码\s*(?:率|资源|直链)?|码\s*率`,
				}
				for i, r := range rules {
					if _, err := tx.Exec(`INSERT INTO magic_episode_clean_regex_rule(pos, pattern) VALUES(?, ?)`, i, r); err != nil {
						return err
					}
				}
				return nil
			},
		},
		{
			Table: "magic_movie_rule",
			Seed: func(tx *sql.Tx) error {
				// Defaults aligned with MeowFilm-Frontend "restore defaults".
				rules := []string{
					`{"pattern":"^\\\\s*(?!.*(?:S\\\\d{1,2}\\\\s*E\\\\d{1,3}|第\\\\s*\\\\d+\\\\s*[集话期]|(?:^|[\\\\s._-])(?:EP?|E)\\\\s*\\\\d+(?:$|[\\\\s._-])))(?=.*\\\\b(?:19\\\\d{2}|20\\\\d{2})\\\\b).*\\\\.(?:mkv|mp4)\\\\s*$","replace":"","flags":"i"}`,
				}
				for i, r := range rules {
					if _, err := tx.Exec(`INSERT INTO magic_movie_rule(pos, rule_text) VALUES(?, ?)`, i, r); err != nil {
						return err
					}
				}
				return nil
			},
		},
		{
			Table: "magic_aggregate_regex_rule",
			Seed: func(tx *sql.Tx) error {
				// Defaults aligned with MeowFilm-Frontend "restore defaults".
				rules := []string{
					`\([^)]*\)|（[^）]*）|\[[^\]]*\]|\{[^}]*\}|【[^】]*】`,
					`(?<!新)年\s*番\s*\d+|(?<!新)年\s*番`,
					`更新\s*中|(?:更新(?:至|到)?|更(?:至|到)?|更|首\s*更)\s*(?:EP|E)?\s*\d{1,4}\s*(?:集|话)?|首\s*更`,
					`(?:HD\s*)?(?:4[kK]|8[kK])|(?:2160|1080|720)[pP]|国\s*漫|臻\s*彩|杜\s*比\s*音\s*效|已\s*刮\s*削|连\s*载\s*中|10\s*[- ]?bit`,
					`(?:19\d{2}|20\d{2})(?=\s*(?:(?:HD\s*)?(?:4[kK]|8[kK])|(?:更新|更)))`,
					`最\s*新\s*(?:一\s*集|更\s*新)`,
					`(?<=\D)\d{1,4}$`,
				}
				for i, r := range rules {
					if _, err := tx.Exec(`INSERT INTO magic_aggregate_regex_rule(pos, pattern) VALUES(?, ?)`, i, r); err != nil {
						return err
					}
				}
				return nil
			},
		},
		{
			Table: "smart_pan_match_token",
			Seed: func(tx *sql.Tx) error {
				tokens := []string{"移动", "天翼", "夸克", "uc", "百度", "115"}
				for i, t := range tokens {
					if _, err := tx.Exec(`INSERT INTO smart_pan_match_token(pos, token) VALUES(?, ?)`, i, t); err != nil {
						return err
					}
				}
				return nil
			},
		},
		{
			Table: "smart_pan_alias_mapping",
			Seed: func(tx *sql.Tx) error {
				rows := []struct {
					Pan     string
					Aliases string
				}{
					{Pan: "百度", Aliases: "百度,baidu"},
					{Pan: "夸克", Aliases: "夸克,quark,夸父"},
					{Pan: "uc", Aliases: "uc,优夕"},
					{Pan: "天翼", Aliases: "天翼,天意,189"},
					{Pan: "移动", Aliases: "移动,139,逸动"},
					{Pan: "115", Aliases: "115,Pan115"},
				}
				for i, it := range rows {
					if _, err := tx.Exec(`INSERT INTO smart_pan_alias_mapping(pos, pan, aliases) VALUES(?, ?, ?)`, i, it.Pan, it.Aliases); err != nil {
						return err
					}
				}
				return nil
			},
		},
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	for _, it := range seeds {
		var cnt int
		if err := tx.QueryRow(`SELECT COUNT(1) FROM ` + it.Table).Scan(&cnt); err != nil {
			return err
		}
		if cnt > 0 {
			continue
		}
		if err := it.Seed(tx); err != nil {
			return err
		}
	}

	if err := tx.Commit(); err != nil {
		return err
	}
	if err := d.runOneTimePanMatchTokenUpgrade(); err != nil {
		return err
	}
	if err := d.runOneTimeAppDoubanSearchCookieMigration(); err != nil {
		return err
	}
	return d.runOneTimeAppRelayGoProxyThresholdMigration()
}

func (d *DB) runOneTimePanMatchTokenUpgrade() error {
	if d == nil || d.db == nil {
		return nil
	}
	const migrationName = "smart_pan_match_token_standardize_v1"

	var done int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM app_migration_flag WHERE name=?`, migrationName).Scan(&done); err != nil {
		return err
	}
	if done > 0 {
		return nil
	}

	rows, err := d.db.Query(`SELECT token FROM smart_pan_match_token ORDER BY pos ASC`)
	if err != nil {
		return err
	}
	defer rows.Close()

	tokens := make([]string, 0)
	for rows.Next() {
		var token sql.NullString
		_ = rows.Scan(&token)
		t := strings.TrimSpace(token.String)
		if t != "" {
			tokens = append(tokens, t)
		}
	}

	legacyToStandard := map[string]string{
		"逸动": "移动",
		"天意": "天翼",
		"夸父": "夸克",
		"优夕": "uc",
	}

	converted := make([]string, 0, len(tokens))
	seen := map[string]bool{}
	changed := false
	for _, t := range tokens {
		nv, ok := legacyToStandard[t]
		next := t
		if ok {
			next = nv
			if next != t {
				changed = true
			}
		}
		key := strings.ToLower(strings.TrimSpace(next))
		if key == "" || seen[key] {
			continue
		}
		seen[key] = true
		converted = append(converted, next)
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if changed {
		if _, err := tx.Exec(`DELETE FROM smart_pan_match_token`); err != nil {
			return err
		}
		for i, t := range converted {
			if _, err := tx.Exec(`INSERT INTO smart_pan_match_token(pos, token) VALUES(?, ?)`, i, t); err != nil {
				return err
			}
		}
	}

	if _, err := tx.Exec(`INSERT INTO app_migration_flag(name, updated_at) VALUES(?, ?)`, migrationName, time.Now().Unix()); err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) runOneTimeAppDoubanSearchCookieMigration() error {
	if d == nil || d.db == nil {
		return nil
	}
	const migrationName = "app_douban_add_search_cookie_v1"

	var done int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM app_migration_flag WHERE name=?`, migrationName).Scan(&done); err != nil {
		return err
	}
	if done > 0 {
		return nil
	}

	rows, err := d.db.Query(`PRAGMA table_info(app_douban)`)
	if err != nil {
		return err
	}
	defer rows.Close()

	hasSearchCookie := false
	for rows.Next() {
		var (
			cid     int
			name    string
			ctype   string
			notnull int
			dflt    sql.NullString
			pk      int
		)
		if err := rows.Scan(&cid, &name, &ctype, &notnull, &dflt, &pk); err != nil {
			return err
		}
		if strings.EqualFold(strings.TrimSpace(name), "search_cookie") {
			hasSearchCookie = true
			break
		}
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if !hasSearchCookie {
		if _, err := tx.Exec(`ALTER TABLE app_douban ADD COLUMN search_cookie TEXT NOT NULL DEFAULT ''`); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(`INSERT INTO app_migration_flag(name, updated_at) VALUES(?, ?)`, migrationName, time.Now().Unix()); err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) runOneTimeAppRelayGoProxyThresholdMigration() error {
	if d == nil || d.db == nil {
		return nil
	}
	const migrationName = "app_relay_add_goproxy_threshold_gb_v1"

	var done int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM app_migration_flag WHERE name=?`, migrationName).Scan(&done); err != nil {
		return err
	}
	if done > 0 {
		return nil
	}

	rows, err := d.db.Query(`PRAGMA table_info(app_relay)`)
	if err != nil {
		return err
	}
	defer rows.Close()

	hasThreshold := false
	for rows.Next() {
		var (
			cid     int
			name    string
			ctype   string
			notnull int
			dflt    sql.NullString
			pk      int
		)
		if err := rows.Scan(&cid, &name, &ctype, &notnull, &dflt, &pk); err != nil {
			return err
		}
		if strings.EqualFold(strings.TrimSpace(name), "goproxy_threshold_gb") {
			hasThreshold = true
			break
		}
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if !hasThreshold {
		if _, err := tx.Exec(`ALTER TABLE app_relay ADD COLUMN goproxy_threshold_gb INTEGER NOT NULL DEFAULT 0`); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(`INSERT INTO app_migration_flag(name, updated_at) VALUES(?, ?)`, migrationName, time.Now().Unix()); err != nil {
		return err
	}
	return tx.Commit()
}
