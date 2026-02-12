package routes

import (
	"crypto/sha1"
	"encoding/hex"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

// JellyfinHandler implements a minimal subset of the Jellyfin API for Infuse.
// Scope (MVP):
// - Auth via /Users/AuthenticateByName -> api_key (token)
// - Series/movie browsing for Infuse
// - PlaybackInfo -> returns a single auto-picked media source
// - Stream proxy endpoint for sources requiring headers
//
// Notes:
// - This is not a full Jellyfin server implementation.
// - Only routes needed by Infuse are exposed; add more incrementally as Infuse requests them.
func JellyfinHandler(database *db.DB) http.Handler {
	serverID := jellyfinStableServerID(defaultString(database.GetSetting("site_name"), "MeowFilm"))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		debugLog := jellyfinDebugLogEnabled()
		lw := &jellyfinLogWriter{ResponseWriter: w, status: 0}

		path := r.URL.Path
		if path == "/jellyfin" || strings.HasPrefix(path, "/jellyfin/") {
			path = strings.TrimPrefix(path, "/jellyfin")
		}
		if path == "" {
			path = "/"
		}
		if !strings.HasPrefix(path, "/") {
			path = "/" + path
		}

		// Basic CORS for LAN usage / Infuse.
		lw.Header().Set("Access-Control-Allow-Origin", "*")
		lw.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Emby-Token, X-MediaBrowser-Token, X-Emby-Authorization, X-MediaBrowser-Authorization")
		lw.Header().Set("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
		if r.Method == http.MethodOptions {
			lw.WriteHeader(http.StatusNoContent)
			if debugLog {
				jellyfinDebugPrintf("[jellyfin] %s %s -> %d (preflight)", r.Method, jellyfinDebugURL(r, true), lw.Status())
			}
			return
		}

		trimmed := strings.Trim(path, "/")
		parts := []string{}
		if trimmed != "" {
			parts = strings.Split(trimmed, "/")
		}

		if len(parts) == 0 {
			http.NotFound(lw, r)
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		}

		switch strings.ToLower(parts[0]) {
		case "system":
			handleJellyfinSystem(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "mediasegments":
			handleJellyfinMediaSegments(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "displaypreferences":
			handleJellyfinDisplayPreferences(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "library":
			handleJellyfinLibrary(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "users":
			handleJellyfinUsers(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "items":
			handleJellyfinItems(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "shows":
			handleJellyfinShows(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "sessions":
			handleJellyfinSessions(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "stream":
			handleJellyfinStream(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		case "videos":
			handleJellyfinVideos(lw, r, database, serverID, parts[1:])
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		default:
			http.NotFound(lw, r)
			if debugLog {
				jellyfinLogDone(r, lw)
			}
			return
		}
	})
}

func jellyfinLogDone(r *http.Request, w *jellyfinLogWriter) {
	if r == nil || w == nil {
		return
	}
	// Default: log url + status + bytes.
	// Only include a sample on errors to reduce noise.
	if w.Status() >= 400 {
		jellyfinDebugPrintf("[jellyfin] %s %s -> %d (%d bytes) %q", r.Method, jellyfinDebugURL(r, true), w.Status(), w.Bytes(), w.Sample())
		return
	}
	jellyfinDebugPrintf("[jellyfin] %s %s -> %d (%d bytes)", r.Method, jellyfinDebugURL(r, true), w.Status(), w.Bytes())
}

func jellyfinDebugURL(r *http.Request, includeQuery bool) string {
	if r == nil {
		return ""
	}
	if !includeQuery {
		return r.URL.Path
	}
	redacted := jellyfinRedactedQuery(r.URL.Query())
	if redacted == "" {
		return r.URL.Path
	}
	return r.URL.Path + "?" + redacted
}

func jellyfinRedactedQuery(q url.Values) string {
	if len(q) == 0 {
		return ""
	}
	keys := make([]string, 0, len(q))
	for k := range q {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	pairs := make([]string, 0, len(keys))
	for _, k := range keys {
		vals := q[k]
		v := ""
		if len(vals) > 0 {
			v = vals[0]
		}
		kl := strings.ToLower(strings.TrimSpace(k))
		if kl == "api_key" || kl == "token" || kl == "access_token" {
			v = "***"
		}
		pairs = append(pairs, url.QueryEscape(k)+"="+url.QueryEscape(v))
	}
	return strings.Join(pairs, "&")
}

type jellyfinLogWriter struct {
	http.ResponseWriter
	status int
	bytes  int
	sample []byte
}

func (w *jellyfinLogWriter) Write(p []byte) (int, error) {
	if w == nil {
		return 0, nil
	}
	if len(p) > 0 {
		w.bytes += len(p)
		if len(w.sample) < 512 {
			remain := 512 - len(w.sample)
			if remain > 0 {
				if len(p) < remain {
					w.sample = append(w.sample, p...)
				} else {
					w.sample = append(w.sample, p[:remain]...)
				}
			}
		}
	}
	return w.ResponseWriter.Write(p)
}

func (w *jellyfinLogWriter) WriteHeader(statusCode int) {
	w.status = statusCode
	w.ResponseWriter.WriteHeader(statusCode)
}

func (w *jellyfinLogWriter) Status() int {
	if w == nil {
		return 0
	}
	if w.status != 0 {
		return w.status
	}
	return http.StatusOK
}

func (w *jellyfinLogWriter) Bytes() int {
	if w == nil {
		return 0
	}
	return w.bytes
}

func (w *jellyfinLogWriter) Sample() string {
	if w == nil || len(w.sample) == 0 {
		return ""
	}
	s := strings.TrimSpace(string(w.sample))
	if s == "" {
		return ""
	}
	return s
}

func jellyfinStableServerID(siteName string) string {
	raw := strings.TrimSpace(siteName)
	if raw == "" {
		raw = "MeowFilm"
	}
	h := sha1.Sum([]byte(raw))
	return hex.EncodeToString(h[:])
}

func jellyfinBaseURL(r *http.Request) string {
	if r == nil {
		return ""
	}
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	// Best-effort: honor reverse proxy headers if present.
	if xf := strings.TrimSpace(r.Header.Get("X-Forwarded-Proto")); xf != "" {
		// Can be "https,http" etc.
		first := strings.TrimSpace(strings.Split(xf, ",")[0])
		if first == "http" || first == "https" {
			scheme = first
		}
	}
	host := strings.TrimSpace(r.Header.Get("X-Forwarded-Host"))
	if host == "" {
		host = r.Host
	}
	host = strings.TrimSpace(host)
	if host == "" {
		return ""
	}
	return scheme + "://" + host
}

func jellyfinWriteError(w http.ResponseWriter, status int, msg string) {
	if status <= 0 {
		status = http.StatusBadRequest
	}
	if strings.TrimSpace(msg) == "" {
		msg = "请求失败"
	}
	writeJSON(w, status, map[string]any{"error": msg})
}

// Jellyfin clients (including Infuse) may send token in:
// - X-Emby-Token
// - X-MediaBrowser-Token
// - Authorization: MediaBrowser Token="...", Client="...", ...
// - query api_key=...
func jellyfinReadToken(r *http.Request) string {
	if r == nil {
		return ""
	}
	if v := strings.TrimSpace(r.URL.Query().Get("api_key")); v != "" {
		return v
	}
	if v := strings.TrimSpace(r.Header.Get("X-Emby-Token")); v != "" {
		return v
	}
	if v := strings.TrimSpace(r.Header.Get("X-MediaBrowser-Token")); v != "" {
		return v
	}
	// Some clients use separate auth headers.
	if v := strings.TrimSpace(jellyfinReadTokenFromAuthHeader(r.Header.Get("X-Emby-Authorization"))); v != "" {
		return v
	}
	if v := strings.TrimSpace(jellyfinReadTokenFromAuthHeader(r.Header.Get("X-MediaBrowser-Authorization"))); v != "" {
		return v
	}
	auth := strings.TrimSpace(r.Header.Get("Authorization"))
	if auth == "" {
		return ""
	}
	return jellyfinReadTokenFromAuthHeader(auth)
}

func jellyfinReadTokenFromAuthHeader(authHeader string) string {
	auth := strings.TrimSpace(authHeader)
	if auth == "" {
		return ""
	}
	low := strings.ToLower(auth)
	if !strings.HasPrefix(low, "mediabrowser") && !strings.HasPrefix(low, "emby") {
		return ""
	}
	// Parse simple "key=value" pairs.
	space := strings.Index(auth, " ")
	if space < 0 {
		return ""
	}
	rest := strings.TrimSpace(auth[space+1:])
	parts := strings.Split(rest, ",")
	for _, p := range parts {
		pp := strings.TrimSpace(p)
		if pp == "" {
			continue
		}
		kv := strings.SplitN(pp, "=", 2)
		if len(kv) != 2 {
			continue
		}
		k := strings.ToLower(strings.TrimSpace(kv[0]))
		v := strings.TrimSpace(kv[1])
		v = strings.Trim(v, "\"")
		if k == "token" && v != "" {
			return v
		}
	}
	return ""
}

// jellyfinUser is a minimal subset used by this integration.
type jellyfinUser struct {
	ID       string
	Username string
	Role     string
	Status   string
}

// jellyfinResolveToken resolves auth_tokens from MeowFilm DB.
func jellyfinResolveToken(database *db.DB, token string) (*jellyfinUser, time.Time, bool) {
	if database == nil {
		return nil, time.Time{}, false
	}
	tok := strings.TrimSpace(token)
	if tok == "" {
		return nil, time.Time{}, false
	}
	var (
		id     int64
		name   string
		role   string
		status string
		expMS  int64
	)
	err := database.SQL().QueryRow(`
		SELECT u.id, u.username, u.role, u.status, t.expires_at
		FROM auth_tokens t JOIN users u ON u.id = t.user_id
		WHERE t.token = ? LIMIT 1
	`, tok).Scan(&id, &name, &role, &status, &expMS)
	if err != nil {
		return nil, time.Time{}, false
	}
	// real user
	return &jellyfinUser{
		ID:       strconv.FormatInt(id, 10),
		Username: strings.TrimSpace(name),
		Role:     strings.TrimSpace(role),
		Status:   strings.TrimSpace(status),
	}, time.UnixMilli(expMS), true
}
