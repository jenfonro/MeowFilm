package emby

import (
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyCurrentUser struct {
	Row            db.UserAuthRow
	ProtocolUserID string
	Token          string
	ExpiresAt      time.Time
}

func readEmbyToken(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"api_key", "token", "X-Emby-Token", "X-MediaBrowser-Token"} {
		if v := strings.TrimSpace(r.URL.Query().Get(key)); v != "" {
			return v
		}
	}
	for _, key := range []string{"X-Emby-Token", "X-MediaBrowser-Token"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return v
		}
	}
	for _, key := range []string{"Authorization", "X-Emby-Authorization", "X-MediaBrowser-Authorization"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			if tok := parseTokenFromAuthorization(v); tok != "" {
				return tok
			}
		}
	}
	if c := strings.TrimSpace(r.Header.Get("Cookie")); c != "" {
		if tok := parseTokenFromCookie(c); tok != "" {
			return tok
		}
	}
	return ""
}

func parseTokenFromAuthorization(v string) string {
	s := strings.TrimSpace(v)
	if s == "" {
		return ""
	}
	low := strings.ToLower(s)
	if strings.HasPrefix(low, "bearer ") {
		return strings.TrimSpace(s[len("bearer "):])
	}
	if strings.HasPrefix(low, "token ") {
		return strings.TrimSpace(s[len("token "):])
	}
	for _, key := range []string{`Token="`, `Token=`} {
		i := strings.Index(s, key)
		if i < 0 {
			continue
		}
		rest := strings.TrimLeft(s[i+len(key):], " ")
		if key == `Token=` {
			rest = strings.TrimPrefix(rest, `"`)
		}
		end := len(rest)
		if j := strings.IndexAny(rest, `",`); j >= 0 {
			end = j
		}
		return strings.TrimSpace(rest[:end])
	}
	return ""
}

func parseTokenFromCookie(cookieHeader string) string {
	for _, p := range strings.Split(strings.TrimSpace(cookieHeader), ";") {
		kv := strings.SplitN(strings.TrimSpace(p), "=", 2)
		if len(kv) != 2 {
			continue
		}
		k := strings.ToLower(strings.TrimSpace(kv[0]))
		v := strings.Trim(strings.TrimSpace(kv[1]), `"`)
		if v == "" {
			continue
		}
		switch k {
		case "x-emby-token", "x-mediabrowser-token", "api_key", "token":
			return v
		}
	}
	return ""
}

func resolveEmbyCurrentUser(database *db.DB, r *http.Request) (*embyCurrentUser, int, string) {
	if database == nil {
		return nil, http.StatusServiceUnavailable, "database unavailable"
	}
	token := readEmbyToken(r)
	if token == "" {
		return nil, http.StatusUnauthorized, "Unauthorized"
	}
	tokenRow, err := database.ResolveToken(token)
	if err != nil || tokenRow == nil {
		if isNoRowsErr(err) || err == nil {
			return nil, http.StatusUnauthorized, "Unauthorized"
		}
		return nil, http.StatusInternalServerError, "请求失败"
	}
	if !tokenRow.ExpiresAt.IsZero() && time.Now().After(tokenRow.ExpiresAt) {
		_ = database.DeleteToken(token)
		return nil, http.StatusUnauthorized, "Unauthorized"
	}
	row, err := database.GetUserAuthByID(tokenRow.UserID)
	if err != nil {
		if isNoRowsErr(err) {
			return nil, http.StatusUnauthorized, "Unauthorized"
		}
		return nil, http.StatusInternalServerError, "请求失败"
	}
	if strings.TrimSpace(row.Status) != "active" {
		return nil, http.StatusForbidden, "该账户已禁用"
	}
	protocolUserID, err := database.GetOrCreateUserProtocolIdentity(row.ID, "emby")
	if err != nil || strings.TrimSpace(protocolUserID) == "" {
		return nil, http.StatusInternalServerError, "请求失败"
	}
	return &embyCurrentUser{
		Row:            row,
		ProtocolUserID: protocolUserID,
		Token:          token,
		ExpiresAt:      tokenRow.ExpiresAt,
	}, 0, ""
}

func writeEmbyAuthError(w http.ResponseWriter, status int, message string) {
	writeEmbyError(w, status, message)
}
