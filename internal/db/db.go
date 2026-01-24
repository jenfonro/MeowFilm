package db

import (
	"database/sql"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"

	_ "github.com/mattn/go-sqlite3"
	"golang.org/x/crypto/bcrypt"
)

type DB struct {
	mu              sync.Mutex
	db              *sql.DB
	settingsCache   map[string]string
	settingsVersion int64
}

func Open() (*DB, error) {
	filePath, fresh, err := resolveDBFile()
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(filePath), 0o755); err != nil {
		return nil, err
	}
	raw, err := sql.Open("sqlite3", filePath+"?_journal_mode=WAL&_busy_timeout=5000")
	if err != nil {
		return nil, err
	}
	if err := raw.Ping(); err != nil {
		_ = raw.Close()
		return nil, err
	}
	d := &DB{db: raw, settingsCache: map[string]string{}}
	if err := d.initSchema(fresh); err != nil {
		_ = raw.Close()
		return nil, err
	}
	return d, nil
}

func resolveDBFile() (filePath string, fresh bool, _ error) {
	if v := strings.TrimSpace(os.Getenv("TV_SERVER_DB_FILE")); v != "" {
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

	dataDir := strings.TrimSpace(os.Getenv("TV_SERVER_DATA_DIR"))
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

	// Prefer a sibling `TV_Server` directory (same default as the Node version).
	sibling := filepath.Clean(filepath.Join(wd, "..", "TV_Server"))
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

func (d *DB) GetSetting(key string) string {
	k := strings.TrimSpace(key)
	if k == "" {
		return ""
	}
	d.mu.Lock()
	if v, ok := d.settingsCache[k]; ok {
		d.mu.Unlock()
		return v
	}
	d.mu.Unlock()

	var v sql.NullString
	_ = d.db.QueryRow(`SELECT value FROM settings WHERE key = ? LIMIT 1`, k).Scan(&v)
	out := ""
	if v.Valid {
		out = v.String
	}
	d.mu.Lock()
	d.settingsCache[k] = out
	d.mu.Unlock()
	return out
}

func (d *DB) SetSetting(key, value string) error {
	k := strings.TrimSpace(key)
	if k == "" {
		return nil
	}
	v := value
	res, err := d.db.Exec(`
		INSERT INTO settings(key, value) VALUES (?, ?)
		ON CONFLICT(key) DO UPDATE SET value = excluded.value
		WHERE settings.value IS NOT excluded.value
	`, k, v)
	if err != nil {
		return err
	}
	changes, _ := res.RowsAffected()

	d.mu.Lock()
	if changes > 0 {
		d.settingsVersion++
	}
	d.settingsCache[k] = v
	d.mu.Unlock()
	return nil
}

func (d *DB) SettingsVersion() int64 {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.settingsVersion
}

func (d *DB) initSchema(fresh bool) error {
	_, err := d.db.Exec(`
		CREATE TABLE IF NOT EXISTS settings (
		  key TEXT PRIMARY KEY,
		  value TEXT
		);
		CREATE TABLE IF NOT EXISTS users (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  username TEXT UNIQUE NOT NULL,
		  password TEXT NOT NULL,
		  role TEXT DEFAULT 'user',
		  status TEXT DEFAULT 'active',
		  cat_api_base TEXT DEFAULT '',
		  cat_api_key TEXT DEFAULT '',
		  cat_proxy TEXT DEFAULT '',
		  search_thread_count INTEGER DEFAULT 5,
		  cat_sites TEXT DEFAULT '[]',
		  cat_site_status TEXT DEFAULT '{}',
		  cat_site_home TEXT DEFAULT '{}',
		  cat_site_order TEXT DEFAULT '[]',
		  cat_site_availability TEXT DEFAULT '{}',
		  cat_search_order TEXT DEFAULT '[]',
		  cat_search_cover_site TEXT DEFAULT ''
		);
		CREATE TABLE IF NOT EXISTS search_history (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  user_id INTEGER NOT NULL,
		  keyword TEXT NOT NULL,
		  updated_at INTEGER NOT NULL,
		  UNIQUE(user_id, keyword)
		);
		CREATE INDEX IF NOT EXISTS idx_search_history_user_id_updated_at ON search_history(user_id, updated_at DESC);
			CREATE TABLE IF NOT EXISTS play_history (
			  id INTEGER PRIMARY KEY AUTOINCREMENT,
			  user_id INTEGER NOT NULL,
			  site_key TEXT NOT NULL,
			  site_name TEXT DEFAULT '',
			  spider_api TEXT NOT NULL,
			  video_id TEXT NOT NULL,
			  video_title TEXT NOT NULL,
			  video_poster TEXT DEFAULT '',
			  video_remark TEXT DEFAULT '',
			  pan_label TEXT DEFAULT '',
			  play_flag TEXT DEFAULT '',
			  content_key TEXT DEFAULT '',
			  episode_index INTEGER DEFAULT 0,
			  episode_name TEXT DEFAULT '',
			  updated_at INTEGER NOT NULL,
			  UNIQUE(user_id, site_key, video_id)
			);
		CREATE INDEX IF NOT EXISTS idx_play_history_user_id_updated_at ON play_history(user_id, updated_at DESC);
		CREATE INDEX IF NOT EXISTS idx_play_history_user_id_content_key_updated_at ON play_history(user_id, content_key, updated_at DESC);
		CREATE TABLE IF NOT EXISTS favorites (
		  id INTEGER PRIMARY KEY AUTOINCREMENT,
		  user_id INTEGER NOT NULL,
		  site_key TEXT NOT NULL,
		  site_name TEXT DEFAULT '',
		  spider_api TEXT NOT NULL,
		  video_id TEXT NOT NULL,
		  video_title TEXT NOT NULL,
		  video_poster TEXT DEFAULT '',
		  video_remark TEXT DEFAULT '',
		  updated_at INTEGER NOT NULL,
		  UNIQUE(user_id, site_key, video_id)
		);
		CREATE INDEX IF NOT EXISTS idx_favorites_user_id_updated_at ON favorites(user_id, updated_at DESC);
		CREATE TABLE IF NOT EXISTS auth_tokens (
		  token TEXT PRIMARY KEY,
		  user_id INTEGER NOT NULL,
		  created_at INTEGER NOT NULL,
		  expires_at INTEGER NOT NULL
		);
		CREATE INDEX IF NOT EXISTS idx_auth_tokens_user_id ON auth_tokens(user_id);
		CREATE INDEX IF NOT EXISTS idx_auth_tokens_expires_at ON auth_tokens(expires_at);
	`)
		if err != nil {
			return err
		}

		// Lightweight schema migrations (SQLite doesn't support IF NOT EXISTS for ADD COLUMN).
		// Keep these idempotent and low-risk for existing installs.
		_ = ensureSQLiteColumn(d.db, "play_history", "pan_label", "TEXT DEFAULT ''")

		if fresh {
			if err := d.seedDefaults(); err != nil {
				return err
			}
		}

	return d.ensureDefaultAdmin()
}

func ensureSQLiteColumn(db *sql.DB, table string, column string, definition string) error {
	if db == nil {
		return nil
	}
	t := strings.TrimSpace(table)
	c := strings.TrimSpace(column)
	def := strings.TrimSpace(definition)
	if t == "" || c == "" || def == "" {
		return nil
	}

	rows, err := db.Query(`PRAGMA table_info(` + t + `)`)
	if err != nil {
		return err
	}
	defer rows.Close()

	for rows.Next() {
		var (
			cid        int
			name       string
			typ        string
			notnull    int
			dfltValue  any
			pk         int
		)
		_ = rows.Scan(&cid, &name, &typ, &notnull, &dfltValue, &pk)
		if strings.EqualFold(strings.TrimSpace(name), c) {
			return nil
		}
	}

	_, err = db.Exec(`ALTER TABLE ` + t + ` ADD COLUMN ` + c + ` ` + def)
	return err
}

func (d *DB) seedDefaults() error {
	type kv struct{ k, v string }
	seeds := []kv{
		{"site_name", "TV Server"},
		{"douban_data_proxy", "direct"},
		{"douban_data_custom", ""},
		{"douban_img_proxy", "direct-browser"},
		{"douban_img_custom", ""},
		{"video_source_url", ""},
		{"video_source_final_url", ""},
		{"video_source_api_base", ""},
		{"video_source_api_headers", ""},
		{"video_source_sites", "[]"},
		{"video_source_md5", ""},
		{"catpawopen_api_base", "http://127.0.0.1:3006/"},
		{"catpawopen_proxy", ""},
		{"openlist_api_base", ""},
		{"openlist_token", ""},
		{"openlist_quark_tv_mode", "0"},
		{"openlist_quark_tv_mount", ""},
		{"video_source_site_status", "{}"},
		{"video_source_site_home", "{}"},
		{"video_source_site_search", "{}"},
		{"video_source_site_order", "[]"},
		{"video_source_site_availability", "{}"},
		{"video_source_site_error", "{}"},
		{"video_source_search_order", "[]"},
		{"video_source_search_cover_site", ""},
		{"magic_episode_rules", `["{\"pattern\":\".*?([Ss]\\\\d{1,2})?(?:[第EePpXx\\\\.\\\\-\\\\_\\\\( ]{1,2}|^)(\\\\d{1,3})(?!\\\\d).*?\\\\.(mp4|mkv)\",\"replace\":\"$1E$2\"}"]`},
		{"magic_episode_clean_regex", ""},
		{"magic_episode_clean_regex_rules", `["\\\\[\\\\s*\\\\d+(?:\\\\.\\\\d+)?\\\\s*(?:B|KB|MB|GB|TB)\\\\s*\\\\]|【[^】]*】"]`},
		{"magic_aggregate_rules", "[]"},
		{"magic_aggregate_regex_rules", "[]"},
		{"goproxy_enabled", "0"},
		{"goproxy_auto_select", "0"},
		{"goproxy_servers", "[]"},
	}
	for _, it := range seeds {
		if _, err := d.db.Exec(`INSERT INTO settings(key,value) VALUES (?,?)`, it.k, it.v); err != nil {
			return err
		}
		d.settingsCache[it.k] = it.v
	}
	return nil
}

func (d *DB) ensureDefaultAdmin() error {
	var cnt int
	if err := d.db.QueryRow(`SELECT COUNT(1) FROM users WHERE role='admin'`).Scan(&cnt); err != nil {
		return err
	}
	if cnt > 0 {
		return nil
	}

	hashed, err := bcrypt.GenerateFromPassword([]byte("admin"), 10)
	if err != nil {
		return err
	}
	_, err = d.db.Exec(`INSERT INTO users(username,password,role,status) VALUES (?,?, 'admin','active')`, "admin", string(hashed))
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
