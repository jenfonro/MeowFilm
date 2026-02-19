package emby

import (
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyUser struct {
	ID       string
	Username string
	Role     string
	Status   string
}

func embyReadToken(r *http.Request) string {
	if r == nil {
		return ""
	}

	// Query (preferred by many clients).
	if v := embyQueryTrimCI(r, "api_key"); v != "" {
		return v
	}
	if v := embyQueryTrimCI(r, "token"); v != "" {
		return v
	}

	// Simple token headers.
	if v := embyHeaderTrim(r, "X-Emby-Token"); v != "" {
		return v
	}
	if v := embyHeaderTrim(r, "X-MediaBrowser-Token"); v != "" {
		return v
	}

	// Authorization-like headers (Emby/Emby style).
	if v := embyHeaderTrim(r, "Authorization"); v != "" {
		if tok := parseTokenFromAuthorization(v); tok != "" {
			return tok
		}
	}
	if v := embyHeaderTrim(r, "X-Emby-Authorization"); v != "" {
		if tok := parseTokenFromAuthorization(v); tok != "" {
			return tok
		}
	}
	if v := embyHeaderTrim(r, "X-MediaBrowser-Authorization"); v != "" {
		if tok := parseTokenFromAuthorization(v); tok != "" {
			return tok
		}
	}

	return ""
}

func parseTokenFromAuthorization(v string) string {
	// Typical forms:
	// - "MediaBrowser Token=\"xxx\", Client=\"...\""
	// - "Emby Token=\"xxx\", Client=\"...\""
	s := strings.TrimSpace(v)
	if s == "" {
		return ""
	}
	// Normalize separators.
	for _, key := range []string{"Token=\"", "Token="} {
		i := strings.Index(s, key)
		if i < 0 {
			continue
		}
		rest := s[i+len(key):]
		rest = strings.TrimLeft(rest, " ")
		if key == "Token=" {
			// Trim possible quote after '='.
			rest = strings.TrimPrefix(rest, "\"")
		}
		// End at quote or comma.
		end := len(rest)
		if j := strings.IndexAny(rest, "\","); j >= 0 {
			end = j
		}
		return strings.TrimSpace(rest[:end])
	}
	return ""
}

func embyResolveToken(database *db.DB, token string) (u *embyUser, exp time.Time, ok bool) {
	if database == nil {
		return nil, time.Time{}, false
	}
	tok := strings.TrimSpace(token)
	if tok == "" {
		return nil, time.Time{}, false
	}
	row, err := database.ResolveToken(tok)
	if err != nil || row == nil {
		return nil, time.Time{}, false
	}
	exp = row.ExpiresAt
	return &embyUser{
		ID:       int64ToStr(row.UserID),
		Username: row.Username,
		Role:     row.Role,
		Status:   row.Status,
	}, exp, true
}
