package routes

import (
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyRequireUser(w http.ResponseWriter, r *http.Request, database *db.DB) (*embyUser, bool) {
	token := embyReadToken(r)
	if token == "" {
		embyWriteError(w, http.StatusUnauthorized, "Unauthorized")
		return nil, false
	}
	u, exp, ok := embyResolveToken(database, token)
	if !ok || u == nil {
		embyWriteError(w, http.StatusUnauthorized, "Unauthorized")
		return nil, false
	}
	if exp.Before(time.Now()) {
		embyWriteError(w, http.StatusUnauthorized, "Token expired")
		return nil, false
	}
	if strings.TrimSpace(u.Status) != "active" {
		embyWriteError(w, http.StatusForbidden, "该账户已禁用")
		return nil, false
	}
	return u, true
}
