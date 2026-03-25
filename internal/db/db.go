package db

import (
	"database/sql"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	_ "github.com/mattn/go-sqlite3"
	"golang.org/x/crypto/bcrypt"
)

type DB struct {
	mu sync.Mutex
	db *sql.DB
}

func Open() (*DB, error) {
	filePath, fresh, err := resolveDBFile()
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(filePath), 0o755); err != nil {
		return nil, err
	}
	raw, err := sql.Open("sqlite3", filePath+"?_journal_mode=WAL&_busy_timeout=5000&_foreign_keys=on")
	if err != nil {
		return nil, err
	}
	if err := raw.Ping(); err != nil {
		_ = raw.Close()
		return nil, err
	}
	d := &DB{db: raw}
	if err := d.initSchema(fresh); err != nil {
		_ = raw.Close()
		return nil, err
	}
	return d, nil
}

func resolveDBFile() (filePath string, fresh bool, _ error) {
	if v := strings.TrimSpace(os.Getenv("MEOWFILM_DB_FILE")); v != "" {
		fp := filepath.Clean(v)
		st, err := os.Stat(fp)
		if err == nil && st.Size() > 0 {
			return fp, false, nil
		}
		if errors.Is(err, os.ErrNotExist) || err == nil {
			return fp, true, nil
		}
		return "", false, err
	}

	dataDir := strings.TrimSpace(os.Getenv("MEOWFILM_DATA_DIR"))
	base := dataDir
	if base == "" {
		base = discoverDefaultDataDir()
	}
	fp := filepath.Join(base, "data.db")
	st, err := os.Stat(fp)
	if err == nil && st.Size() > 0 {
		return fp, false, nil
	}
	if errors.Is(err, os.ErrNotExist) || err == nil {
		return fp, true, nil
	}
	return "", false, err
}

func discoverDefaultDataDir() string {
	wd, err := os.Getwd()
	if err != nil || wd == "" {
		return "."
	}

	// Prefer a sibling project directory when running from a subfolder.
	sibling := filepath.Clean(filepath.Join(wd, "..", "MeowFilm"))
	if st, err := os.Stat(sibling); err == nil && st.IsDir() {
		if isDir(filepath.Join(sibling, "server")) && isDir(filepath.Join(sibling, "web")) {
			return sibling
		}
	}
	return wd
}

func isDir(p string) bool {
	st, err := os.Stat(p)
	return err == nil && st.IsDir()
}

func (d *DB) Close() error {
	if d == nil || d.db == nil {
		return nil
	}
	return d.db.Close()
}

func (d *DB) SQL() *sql.DB { return d.db }

func (d *DB) initSchema(fresh bool) error {
	if err := d.ensureSchema(); err != nil {
		return err
	}
	// Seed defaults if this is a fresh DB, or if the normalized config row is missing.
	if err := d.ensureDefaults(fresh); err != nil {
		return err
	}

	return d.ensureDefaultAdmin()
}

func (d *DB) ensureSchema() error {
	if d == nil || d.db == nil {
		return nil
	}

	// No migration/compat layer: always ensure the latest schema exists.
	schemaSQL := `
				CREATE TABLE IF NOT EXISTS users (
				  id INTEGER PRIMARY KEY AUTOINCREMENT,
				  username TEXT UNIQUE NOT NULL,
				  password TEXT NOT NULL,
				  role TEXT NOT NULL DEFAULT 'user' CHECK (role IN ('admin','user')),
				  status TEXT DEFAULT 'active',
				  created_at INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL DEFAULT 0
				);
				CREATE INDEX IF NOT EXISTS idx_users_role_status ON users(role, status);

					-- Search history (3NF)
					CREATE TABLE IF NOT EXISTS search_keyword (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  keyword TEXT UNIQUE NOT NULL,
					  created_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL DEFAULT 0
					);
					CREATE TABLE IF NOT EXISTS user_search_history (
					  user_id INTEGER NOT NULL,
					  keyword_id INTEGER NOT NULL,
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(user_id, keyword_id),
					  FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
					  FOREIGN KEY(keyword_id) REFERENCES search_keyword(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_user_search_history_user_updated_at ON user_search_history(user_id, updated_at DESC);

					-- Content registry (3NF)
					CREATE TABLE IF NOT EXISTS content (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  content_key TEXT UNIQUE NOT NULL,
					  created_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL DEFAULT 0
					);
					CREATE TABLE IF NOT EXISTS content_tmdb (
					  content_id INTEGER PRIMARY KEY,
					  tmdb_id INTEGER NOT NULL,
					  tmdb_type TEXT NOT NULL,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(tmdb_type, tmdb_id),
					  FOREIGN KEY(content_id) REFERENCES content(id) ON DELETE CASCADE
					);

					-- TMDB library (normalized, multi-language ready)
					CREATE TABLE IF NOT EXISTS tmdb_media (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  tmdb_type TEXT NOT NULL, -- 'tv' | 'movie'
					  tmdb_id INTEGER NOT NULL,
					  poster_path TEXT NOT NULL DEFAULT '',
					  backdrop_path TEXT NOT NULL DEFAULT '',
					  status TEXT NOT NULL DEFAULT '',
					  first_air_date TEXT NOT NULL DEFAULT '',
					  release_date TEXT NOT NULL DEFAULT '',
					  runtime INTEGER NOT NULL DEFAULT 0,
					  last_access_at INTEGER NOT NULL DEFAULT 0,
					  last_refresh_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(tmdb_type, tmdb_id)
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_media_type_id ON tmdb_media(tmdb_type, tmdb_id);
					CREATE TABLE IF NOT EXISTS tmdb_media_i18n (
					  media_id INTEGER NOT NULL,
					  lang TEXT NOT NULL, -- e.g. 'zh-CN'
					  title TEXT NOT NULL DEFAULT '',
					  original_title TEXT NOT NULL DEFAULT '',
					  overview TEXT NOT NULL DEFAULT '',
					  tagline TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(media_id, lang),
					  FOREIGN KEY(media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_media_i18n_lang ON tmdb_media_i18n(lang);

					CREATE TABLE IF NOT EXISTS tmdb_season (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  media_id INTEGER NOT NULL,
					  season_number INTEGER NOT NULL,
					  air_date TEXT NOT NULL DEFAULT '',
					  poster_path TEXT NOT NULL DEFAULT '',
					  episode_count INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(media_id, season_number),
					  FOREIGN KEY(media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_season_media ON tmdb_season(media_id, season_number);
					CREATE TABLE IF NOT EXISTS tmdb_season_i18n (
					  season_id INTEGER NOT NULL,
					  lang TEXT NOT NULL,
					  name TEXT NOT NULL DEFAULT '',
					  overview TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(season_id, lang),
					  FOREIGN KEY(season_id) REFERENCES tmdb_season(id) ON DELETE CASCADE
					);

					CREATE TABLE IF NOT EXISTS tmdb_episode (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  media_id INTEGER NOT NULL,
					  season_number INTEGER NOT NULL,
					  episode_number INTEGER NOT NULL,
					  air_date TEXT NOT NULL DEFAULT '',
					  runtime INTEGER NOT NULL DEFAULT 0,
					  still_path TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  UNIQUE(media_id, season_number, episode_number),
					  FOREIGN KEY(media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_episode_media ON tmdb_episode(media_id, season_number, episode_number);
					CREATE TABLE IF NOT EXISTS tmdb_episode_i18n (
					  episode_id INTEGER NOT NULL,
					  lang TEXT NOT NULL,
					  name TEXT NOT NULL DEFAULT '',
					  overview TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(episode_id, lang),
					  FOREIGN KEY(episode_id) REFERENCES tmdb_episode(id) ON DELETE CASCADE
					);

					-- Season hints/overrides from other sources (e.g. Douban)
					CREATE TABLE IF NOT EXISTS tmdb_season_hint (
					  media_id INTEGER NOT NULL,
					  source TEXT NOT NULL, -- 'douban' | ...
					  season_number INTEGER NOT NULL,
					  episode_count INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(media_id, source, season_number),
					  FOREIGN KEY(media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_season_hint_media_source ON tmdb_season_hint(media_id, source);

					-- External IDs (Douban/IMDB/TVDB/...) - flexible mapping
					CREATE TABLE IF NOT EXISTS tmdb_external_id (
					  media_id INTEGER NOT NULL,
					  source TEXT NOT NULL, -- 'douban' | 'imdb' | 'tvdb' ...
					  external_id TEXT NOT NULL,
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(media_id, source),
					  UNIQUE(source, external_id),
					  FOREIGN KEY(media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_tmdb_external_id_source ON tmdb_external_id(source, external_id);

					-- Site video (3NF)
					-- site_kind: 'global' | 'emby' (extensible)
					-- owner_user_id: reserved for future scoping (currently always 0)
					CREATE TABLE IF NOT EXISTS site_video (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  site_kind TEXT NOT NULL DEFAULT 'global',
					  owner_user_id INTEGER NOT NULL DEFAULT 0,
					  site_key TEXT NOT NULL,
					  site_detail TEXT NOT NULL,
					  title TEXT NOT NULL,
					  poster TEXT NOT NULL DEFAULT '',
					  remark TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  UNIQUE(site_kind, owner_user_id, site_key, site_detail)
					);
					CREATE INDEX IF NOT EXISTS idx_site_video_site ON site_video(site_kind, owner_user_id, site_key);

						-- Playback history (3NF)
						CREATE TABLE IF NOT EXISTS user_play_history (
						  id INTEGER PRIMARY KEY AUTOINCREMENT,
						  user_id INTEGER NOT NULL,
						  content_id INTEGER NOT NULL,
						  site_video_id INTEGER NOT NULL,
						  play_flag TEXT NOT NULL DEFAULT '',
						  site_episode_index INTEGER NOT NULL DEFAULT 0,
						  site_episode_file TEXT NOT NULL DEFAULT '',
						  tmdb_season INTEGER NOT NULL DEFAULT 0,
						  tmdb_episode INTEGER NOT NULL DEFAULT 0,
						  playback_position_ticks INTEGER NOT NULL DEFAULT 0,
						  playback_runtime_ticks INTEGER NOT NULL DEFAULT 0,
						  playback_item_id TEXT NOT NULL DEFAULT '',
						  updated_at INTEGER NOT NULL,
						  UNIQUE(user_id, site_video_id),
						  FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
					  FOREIGN KEY(content_id) REFERENCES content(id) ON DELETE CASCADE,
					  FOREIGN KEY(site_video_id) REFERENCES site_video(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_user_play_history_user_updated_at ON user_play_history(user_id, updated_at DESC);
					CREATE INDEX IF NOT EXISTS idx_user_play_history_user_content_updated_at ON user_play_history(user_id, content_id, updated_at DESC);
					CREATE INDEX IF NOT EXISTS idx_user_play_history_user_playback_item ON user_play_history(user_id, playback_item_id);

					-- Favorites (3NF)
					CREATE TABLE IF NOT EXISTS user_favorite (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  user_id INTEGER NOT NULL,
					  site_video_id INTEGER NOT NULL,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(user_id, site_video_id),
					  FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
					  FOREIGN KEY(site_video_id) REFERENCES site_video(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_user_favorite_user_updated_at ON user_favorite(user_id, updated_at DESC);

					CREATE TABLE IF NOT EXISTS auth_tokens (
					  token TEXT PRIMARY KEY,
					  user_id INTEGER NOT NULL,
					  created_at INTEGER NOT NULL,
					  expires_at INTEGER NOT NULL
					);
				CREATE INDEX IF NOT EXISTS idx_auth_tokens_user_id ON auth_tokens(user_id);
				CREATE INDEX IF NOT EXISTS idx_auth_tokens_expires_at ON auth_tokens(expires_at);
					CREATE TABLE IF NOT EXISTS catpawrunner_server (
					  name TEXT PRIMARY KEY,
					  api_base TEXT NOT NULL,
					  order_index INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL
				);
				CREATE INDEX IF NOT EXISTS idx_catpawrunner_server_order ON catpawrunner_server(order_index);
				CREATE TABLE IF NOT EXISTS catpawrunner_pan (
				  key TEXT PRIMARY KEY,
				  name TEXT NOT NULL DEFAULT '',
				  enabled INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL
				);
				CREATE TABLE IF NOT EXISTS goproxy_server (
				  name TEXT PRIMARY KEY,
				  display_name TEXT NOT NULL DEFAULT '',
				  base TEXT NOT NULL,
				  pans_baidu INTEGER NOT NULL DEFAULT 0,
				  pans_quark INTEGER NOT NULL DEFAULT 0,
				  order_index INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL
				);
				CREATE INDEX IF NOT EXISTS idx_goproxy_server_order ON goproxy_server(order_index);
				CREATE TABLE IF NOT EXISTS relay_server (
				  name TEXT PRIMARY KEY,
				  display_name TEXT NOT NULL DEFAULT '',
				  base TEXT NOT NULL,
				  secret TEXT NOT NULL DEFAULT '',
				  pans_baidu INTEGER NOT NULL DEFAULT 0,
				  pans_quark INTEGER NOT NULL DEFAULT 0,
				  order_index INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL
				);
				CREATE INDEX IF NOT EXISTS idx_relay_server_order ON relay_server(order_index);
				CREATE TABLE IF NOT EXISTS video_source_site (
				  key TEXT PRIMARY KEY,
				  name TEXT NOT NULL DEFAULT '',
				  api TEXT NOT NULL,
				  type INTEGER,
				  updated_at INTEGER NOT NULL
				);
				CREATE TABLE IF NOT EXISTS video_source_site_state (
				  site_key TEXT PRIMARY KEY,
				  enabled INTEGER NOT NULL DEFAULT 1,
				  home INTEGER NOT NULL DEFAULT 0,
				  search INTEGER NOT NULL DEFAULT 0,
				  smart_skip INTEGER NOT NULL DEFAULT 0,
				  availability TEXT NOT NULL DEFAULT 'unchecked',
				  error TEXT NOT NULL DEFAULT '',
				  order_index INTEGER NOT NULL DEFAULT 0,
				  updated_at INTEGER NOT NULL,
				  FOREIGN KEY(site_key) REFERENCES video_source_site(key) ON DELETE CASCADE
				);
				CREATE INDEX IF NOT EXISTS idx_video_source_site_state_order ON video_source_site_state(order_index);
				CREATE TABLE IF NOT EXISTS magic_episode_rule (
				  pos INTEGER PRIMARY KEY,
				  rule_text TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS magic_episode_clean_regex_rule (
				  pos INTEGER PRIMARY KEY,
				  pattern TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS magic_movie_rule (
				  pos INTEGER PRIMARY KEY,
				  rule_text TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS magic_aggregate_rule (
				  pos INTEGER PRIMARY KEY,
				  rule_text TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS magic_aggregate_regex_rule (
				  pos INTEGER PRIMARY KEY,
				  pattern TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS smart_source_priority_token (
				  pos INTEGER PRIMARY KEY,
				  token TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS smart_pan_match_token (
				  pos INTEGER PRIMARY KEY,
				  token TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS smart_pan_alias_mapping (
				  pos INTEGER PRIMARY KEY,
				  pan TEXT NOT NULL,
				  aliases TEXT NOT NULL DEFAULT ''
				);
				CREATE TABLE IF NOT EXISTS smart_match_block_keyword (
				  id INTEGER PRIMARY KEY AUTOINCREMENT,
				  keyword TEXT NOT NULL,
				  created_at INTEGER NOT NULL,
				  updated_at INTEGER NOT NULL,
				  UNIQUE(keyword)
				);
				CREATE INDEX IF NOT EXISTS idx_smart_match_block_keyword_updated_at ON smart_match_block_keyword(updated_at DESC);
				CREATE TABLE IF NOT EXISTS smart_match_block_item (
				  id INTEGER PRIMARY KEY AUTOINCREMENT,
				  keyword_id INTEGER NOT NULL,
				  site_key TEXT NOT NULL,
				  spider_api TEXT NOT NULL DEFAULT '',
				  site_detail TEXT NOT NULL,
				  poster TEXT NOT NULL DEFAULT '',
				  pan_flag TEXT NOT NULL DEFAULT '',
				  source TEXT NOT NULL DEFAULT 'search',
				  created_at INTEGER NOT NULL,
				  updated_at INTEGER NOT NULL,
				  UNIQUE(keyword_id, site_key, site_detail, pan_flag, source),
				  FOREIGN KEY(keyword_id) REFERENCES smart_match_block_keyword(id) ON DELETE CASCADE
				);
				CREATE INDEX IF NOT EXISTS idx_smart_match_block_item_keyword_site_video ON smart_match_block_item(keyword_id, site_key, site_detail, pan_flag, source);
				CREATE INDEX IF NOT EXISTS idx_smart_match_block_item_keyword_updated_at ON smart_match_block_item(keyword_id, updated_at DESC);
				CREATE TABLE IF NOT EXISTS pan_login_setting (
				  provider TEXT NOT NULL,
				  field TEXT NOT NULL,
				  value TEXT NOT NULL,
				  PRIMARY KEY(provider, field)
				);
				CREATE INDEX IF NOT EXISTS idx_pan_login_setting_provider ON pan_login_setting(provider);
					-- Douban media & TMDB link (normalized, multi-language ready)
					CREATE TABLE IF NOT EXISTS douban_media (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  kind TEXT NOT NULL, -- "movie" | "tv"
					  douban_id TEXT NOT NULL,
					  year INTEGER NOT NULL DEFAULT 0,
					  created_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(kind, douban_id)
					);
					CREATE INDEX IF NOT EXISTS idx_douban_media_kind_updated_at ON douban_media(kind, updated_at DESC);
					CREATE TABLE IF NOT EXISTS douban_media_i18n (
					  media_id INTEGER NOT NULL,
					  lang TEXT NOT NULL, -- e.g. 'zh-CN'
					  title TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(media_id, lang),
					  FOREIGN KEY(media_id) REFERENCES douban_media(id) ON DELETE CASCADE
					);
					CREATE TABLE IF NOT EXISTS douban_media_state (
					  media_id INTEGER PRIMARY KEY,
					  last_try_at INTEGER NOT NULL DEFAULT 0,
					  last_try_key TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  FOREIGN KEY(media_id) REFERENCES douban_media(id) ON DELETE CASCADE
					);
					CREATE TABLE IF NOT EXISTS douban_detail_cache (
					  kind TEXT NOT NULL,
					  douban_id TEXT NOT NULL,
					  payload_json TEXT NOT NULL DEFAULT '',
					  last_access_at INTEGER NOT NULL DEFAULT 0,
					  last_refresh_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(kind, douban_id)
					);
					CREATE INDEX IF NOT EXISTS idx_douban_detail_cache_access ON douban_detail_cache(last_access_at DESC);
					CREATE TABLE IF NOT EXISTS douban_tmdb_link (
					  douban_media_id INTEGER NOT NULL,
					  tmdb_media_id INTEGER NOT NULL,
					  updated_at INTEGER NOT NULL,
					  PRIMARY KEY(douban_media_id),
					  FOREIGN KEY(douban_media_id) REFERENCES douban_media(id) ON DELETE CASCADE,
					  FOREIGN KEY(tmdb_media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_douban_tmdb_link_tmdb_media ON douban_tmdb_link(tmdb_media_id);

					-- Cache domain (fully normalized)
					CREATE TABLE IF NOT EXISTS cache_site_pan (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  site_kind TEXT NOT NULL DEFAULT 'global', -- 'global' | 'user' | 'emby'
					  owner_user_id INTEGER NOT NULL DEFAULT 0,
					  site_id TEXT NOT NULL,
					  site_pan_id TEXT NOT NULL,
					  spider_api TEXT NOT NULL,
					  site_detail TEXT NOT NULL,
					  pan_flag TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  UNIQUE(site_kind, owner_user_id, site_id, site_pan_id)
					);
					CREATE INDEX IF NOT EXISTS idx_cache_site_pan_site ON cache_site_pan(site_kind, owner_user_id, site_id, updated_at DESC);
					CREATE TABLE IF NOT EXISTS cache_site_pan_state (
					  site_pan_id INTEGER PRIMARY KEY,
					  status TEXT NOT NULL DEFAULT 'active',
					  fail_count INTEGER NOT NULL DEFAULT 0,
					  cooldown_until INTEGER NOT NULL DEFAULT 0,
					  last_refresh_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  FOREIGN KEY(site_pan_id) REFERENCES cache_site_pan(id) ON DELETE CASCADE
					);

					CREATE TABLE IF NOT EXISTS cache_pan (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  provider TEXT NOT NULL,
					  pan_id TEXT NOT NULL,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(provider, pan_id)
					);
					CREATE INDEX IF NOT EXISTS idx_cache_pan_provider ON cache_pan(provider, updated_at DESC);
					CREATE TABLE IF NOT EXISTS cache_pan_state (
					  pan_id INTEGER PRIMARY KEY,
					  status TEXT NOT NULL DEFAULT 'active',
					  fail_count INTEGER NOT NULL DEFAULT 0,
					  cooldown_until INTEGER NOT NULL DEFAULT 0,
					  last_verified_at INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  FOREIGN KEY(pan_id) REFERENCES cache_pan(id) ON DELETE CASCADE
					);
					CREATE TABLE IF NOT EXISTS cache_pan_play_hint (
					  pan_id INTEGER PRIMARY KEY,
					  play_flag TEXT NOT NULL DEFAULT '',
					  play_share_url TEXT NOT NULL DEFAULT '',
					  play_filename TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL,
					  FOREIGN KEY(pan_id) REFERENCES cache_pan(id) ON DELETE CASCADE
					);

					CREATE TABLE IF NOT EXISTS cache_episode (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  tmdb_media_id INTEGER NOT NULL,
					  season INTEGER NOT NULL DEFAULT 0,
					  episode INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(tmdb_media_id, season, episode),
					  FOREIGN KEY(tmdb_media_id) REFERENCES tmdb_media(id) ON DELETE CASCADE
					);
					CREATE INDEX IF NOT EXISTS idx_cache_episode_lookup ON cache_episode(tmdb_media_id, season, episode);

					CREATE TABLE IF NOT EXISTS cache_quality (
					  id INTEGER PRIMARY KEY AUTOINCREMENT,
					  resolution TEXT NOT NULL DEFAULT '',
					  codec TEXT NOT NULL DEFAULT '',
					  hdr INTEGER NOT NULL DEFAULT 0,
					  fps INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL,
					  UNIQUE(resolution, codec, hdr, fps)
					);
					CREATE TABLE IF NOT EXISTS cache_candidate (
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
						);
						CREATE INDEX IF NOT EXISTS idx_cache_candidate_episode ON cache_candidate(episode_id, updated_at DESC);
						CREATE INDEX IF NOT EXISTS idx_cache_candidate_cooldown ON cache_candidate(episode_id, cooldown_until);

					-- Normalized config & user preferences (no legacy KV table).
						-- App config (fully split by domain; single-row per domain)
						CREATE TABLE IF NOT EXISTS app_site (
						  id INTEGER PRIMARY KEY CHECK (id = 1),
						  site_name TEXT NOT NULL DEFAULT 'MeowFilm',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_douban (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  data_proxy TEXT NOT NULL DEFAULT 'server-proxy',
					  data_custom TEXT NOT NULL DEFAULT '',
					  img_proxy TEXT NOT NULL DEFAULT 'server-proxy',
					  img_custom TEXT NOT NULL DEFAULT '',
					  search_cookie TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_tmdb (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  api_token TEXT NOT NULL DEFAULT '',
					  api_base TEXT NOT NULL DEFAULT '',
					  img_base TEXT NOT NULL DEFAULT '',
					  language TEXT NOT NULL DEFAULT 'zh-CN',
					  region TEXT NOT NULL DEFAULT 'CN',
					  include_adult INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_video_source (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  api_base TEXT NOT NULL DEFAULT '',
					  search_cover_site TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_search (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  display_mode TEXT NOT NULL DEFAULT 'sites',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_smart (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  source_extract_priority TEXT NOT NULL DEFAULT '无',
					  site_clean_keywords TEXT NOT NULL DEFAULT '直播,体育,短剧,听书,舞曲,哔哩',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_goproxy (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  enabled INTEGER NOT NULL DEFAULT 0,
					  auto_select INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_relay (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  enabled INTEGER NOT NULL DEFAULT 0,
					  relay_token TEXT NOT NULL DEFAULT '',
					  goproxy_threshold_gb INTEGER NOT NULL DEFAULT 0,
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_netdisk_proxy (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  enabled INTEGER NOT NULL DEFAULT 0,
					  proxy_url TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_catpawrunner (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  active TEXT NOT NULL DEFAULT '',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_emby (
					  id INTEGER PRIMARY KEY CHECK (id = 1),
					  home_sections_json TEXT NOT NULL DEFAULT '[]',
					  updated_at INTEGER NOT NULL
					);
					CREATE TABLE IF NOT EXISTS app_migration_flag (
					  name TEXT PRIMARY KEY,
					  updated_at INTEGER NOT NULL
					);
	`
	schemaSQL += extraSchemaSQL()

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(schemaSQL); err != nil {
		return err
	}
	if err := ensureSmartMatchBlockItemSchema(tx); err != nil {
		return err
	}
	if err := ensureSiteVideoSchema(tx); err != nil {
		return err
	}
	if err := ensureCacheSitePanSchema(tx); err != nil {
		return err
	}
	if err := ensureUserPlayHistorySchema(tx); err != nil {
		return err
	}
	if err := ensureTMDBMediaCacheSchema(tx); err != nil {
		return err
	}
	return tx.Commit()
}

func ensureTMDBMediaCacheSchema(tx *sql.Tx) error {
	if tx == nil {
		return nil
	}
	rows, err := tx.Query(`PRAGMA table_info(tmdb_media)`)
	if err != nil {
		return nil
	}
	defer rows.Close()

	cols := map[string]bool{}
	for rows.Next() {
		var (
			cid        int
			name       string
			typ        string
			notnull    int
			dfltValue  any
			primaryKey int
		)
		_ = rows.Scan(&cid, &name, &typ, &notnull, &dfltValue, &primaryKey)
		if name != "" {
			cols[name] = true
		}
	}
	if !cols["last_access_at"] {
		if _, err := tx.Exec(`ALTER TABLE tmdb_media ADD COLUMN last_access_at INTEGER NOT NULL DEFAULT 0`); err != nil {
			return err
		}
	}
	if !cols["last_refresh_at"] {
		if _, err := tx.Exec(`ALTER TABLE tmdb_media ADD COLUMN last_refresh_at INTEGER NOT NULL DEFAULT 0`); err != nil {
			return err
		}
	}
	if _, err := tx.Exec(`
		UPDATE tmdb_media
		SET last_access_at = CASE
			WHEN last_access_at <= 0 THEN updated_at
			ELSE last_access_at
		END,
		last_refresh_at = CASE
			WHEN last_refresh_at <= 0 THEN updated_at
			ELSE last_refresh_at
		END
	`); err != nil {
		return err
	}
	return nil
}

func hasTxMigrationFlag(tx *sql.Tx, name string) (bool, error) {
	if tx == nil || strings.TrimSpace(name) == "" {
		return false, nil
	}
	var done int
	if err := tx.QueryRow(`SELECT COUNT(1) FROM app_migration_flag WHERE name=?`, strings.TrimSpace(name)).Scan(&done); err != nil {
		return false, err
	}
	return done > 0, nil
}

func markTxMigrationFlag(tx *sql.Tx, name string) error {
	if tx == nil || strings.TrimSpace(name) == "" {
		return nil
	}
	_, err := tx.Exec(`INSERT INTO app_migration_flag(name, updated_at) VALUES(?, ?)`, strings.TrimSpace(name), time.Now().Unix())
	return err
}

func ensureSmartMatchBlockItemSchema(tx *sql.Tx) error {
	const migrationName = "schema_reset_smart_match_block_item_v1"
	if tx == nil {
		return nil
	}
	if done, err := hasTxMigrationFlag(tx, migrationName); err == nil && done {
		return nil
	}
	if _, err := tx.Exec(`DROP TABLE IF EXISTS smart_match_block_item`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE smart_match_block_item (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  keyword_id INTEGER NOT NULL,
		  site_key TEXT NOT NULL,
		  spider_api TEXT NOT NULL DEFAULT '',
		  site_detail TEXT NOT NULL,
		  poster TEXT NOT NULL DEFAULT '',
		  pan_flag TEXT NOT NULL DEFAULT '',
		  source TEXT NOT NULL DEFAULT 'search',
		  created_at INTEGER NOT NULL,
		  updated_at INTEGER NOT NULL,
		  UNIQUE(keyword_id, site_key, site_detail, pan_flag, source),
		  FOREIGN KEY(keyword_id) REFERENCES smart_match_block_keyword(id) ON DELETE CASCADE
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_smart_match_block_item_keyword_site_video ON smart_match_block_item(keyword_id, site_key, site_detail, pan_flag, source)`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_smart_match_block_item_keyword_updated_at ON smart_match_block_item(keyword_id, updated_at DESC)`); err != nil {
		return err
	}
	return markTxMigrationFlag(tx, migrationName)
}

func ensureSiteVideoSchema(tx *sql.Tx) error {
	const migrationName = "schema_reset_site_video_v1"
	if tx == nil {
		return nil
	}
	if done, err := hasTxMigrationFlag(tx, migrationName); err == nil && done {
		return nil
	}
	if _, err := tx.Exec(`DROP TABLE IF EXISTS site_video`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE site_video (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  site_kind TEXT NOT NULL DEFAULT 'global',
		  owner_user_id INTEGER NOT NULL DEFAULT 0,
		  site_key TEXT NOT NULL,
		  site_detail TEXT NOT NULL,
		  title TEXT NOT NULL,
		  poster TEXT NOT NULL DEFAULT '',
		  remark TEXT NOT NULL DEFAULT '',
		  updated_at INTEGER NOT NULL,
		  UNIQUE(site_kind, owner_user_id, site_key, site_detail)
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_site_video_site ON site_video(site_kind, owner_user_id, site_key)`); err != nil {
		return err
	}
	return markTxMigrationFlag(tx, migrationName)
}

func ensureCacheSitePanSchema(tx *sql.Tx) error {
	const migrationName = "schema_reset_cache_site_pan_v1"
	if tx == nil {
		return nil
	}
	if done, err := hasTxMigrationFlag(tx, migrationName); err == nil && done {
		return nil
	}
	if _, err := tx.Exec(`DROP TABLE IF EXISTS cache_site_pan`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE cache_site_pan (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  site_kind TEXT NOT NULL DEFAULT 'global',
		  owner_user_id INTEGER NOT NULL DEFAULT 0,
		  site_id TEXT NOT NULL,
		  site_pan_id TEXT NOT NULL,
		  spider_api TEXT NOT NULL,
		  site_detail TEXT NOT NULL,
		  pan_flag TEXT NOT NULL DEFAULT '',
		  updated_at INTEGER NOT NULL,
		  UNIQUE(site_kind, owner_user_id, site_id, site_pan_id)
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_cache_site_pan_site ON cache_site_pan(site_kind, owner_user_id, site_id, updated_at DESC)`); err != nil {
		return err
	}
	return markTxMigrationFlag(tx, migrationName)
}

func ensureUserPlayHistorySchema(tx *sql.Tx) error {
	const migrationName = "schema_reset_user_play_history_v1"
	if tx == nil {
		return nil
	}
	if done, err := hasTxMigrationFlag(tx, migrationName); err == nil && done {
		return nil
	}
	if _, err := tx.Exec(`DROP TABLE IF EXISTS user_play_history`); err != nil {
		return err
	}
	if _, err := tx.Exec(`
		CREATE TABLE user_play_history (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  user_id INTEGER NOT NULL,
		  content_id INTEGER NOT NULL,
		  site_video_id INTEGER NOT NULL,
		  play_flag TEXT NOT NULL DEFAULT '',
		  site_episode_index INTEGER NOT NULL DEFAULT 0,
		  site_episode_file TEXT NOT NULL DEFAULT '',
		  tmdb_season INTEGER NOT NULL DEFAULT 0,
		  tmdb_episode INTEGER NOT NULL DEFAULT 0,
		  playback_position_ticks INTEGER NOT NULL DEFAULT 0,
		  playback_runtime_ticks INTEGER NOT NULL DEFAULT 0,
		  playback_item_id TEXT NOT NULL DEFAULT '',
		  updated_at INTEGER NOT NULL,
		  UNIQUE(user_id, site_video_id),
		  FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
		  FOREIGN KEY(content_id) REFERENCES content(id) ON DELETE CASCADE,
		  FOREIGN KEY(site_video_id) REFERENCES site_video(id) ON DELETE CASCADE
		)
	`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_user_play_history_user_updated_at ON user_play_history(user_id, updated_at DESC)`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_user_play_history_user_content_updated_at ON user_play_history(user_id, content_id, updated_at DESC)`); err != nil {
		return err
	}
	if _, err := tx.Exec(`CREATE INDEX IF NOT EXISTS idx_user_play_history_user_playback_item ON user_play_history(user_id, playback_item_id)`); err != nil {
		return err
	}
	return markTxMigrationFlag(tx, migrationName)
}

func (d *DB) ensureDefaultAdmin() error {
	var cnt int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM users WHERE role='admin'`).Scan(&cnt); err != nil {
		return err
	}
	if cnt > 0 {
		return nil
	}

	if err := d.EnforceUsersLimitBeforeInsert(); err != nil {
		return err
	}
	hashed, err := bcrypt.GenerateFromPassword([]byte("admin"), 10)
	if err != nil {
		return err
	}
	now := time.Now().Unix()
	res, err := d.db.Exec(`INSERT INTO users(username,password,role,status,created_at,updated_at) VALUES (?,?, 'admin','active',?,?)`, "admin", string(hashed), now, now)
	if err != nil {
		return err
	}
	_, _ = res.LastInsertId()
	return err
}

func ParseBool01(v string) bool { return strings.TrimSpace(v) == "1" }
func ParseIntDefault(v string, def int) int {
	v = strings.TrimSpace(v)
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return def
	}
	return n
}
