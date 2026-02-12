package routes

import (
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func jellyfinRequireUser(w http.ResponseWriter, r *http.Request, database *db.DB) (*jellyfinUser, bool) {
	token := jellyfinReadToken(r)
	if token == "" {
		jellyfinWriteError(w, http.StatusUnauthorized, "Unauthorized")
		return nil, false
	}
	u, exp, ok := jellyfinResolveToken(database, token)
	if !ok || u == nil {
		jellyfinWriteError(w, http.StatusUnauthorized, "Unauthorized")
		return nil, false
	}
	if exp.Before(time.Now()) {
		jellyfinWriteError(w, http.StatusUnauthorized, "Token expired")
		return nil, false
	}
	if strings.TrimSpace(u.Status) != "active" {
		jellyfinWriteError(w, http.StatusForbidden, "该账户已禁用")
		return nil, false
	}
	return u, true
}

