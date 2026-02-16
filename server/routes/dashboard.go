package routes

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/dashboard"
)

func DashboardHandler(database *db.DB, authMw *auth.Auth) http.Handler {
	return dashboard.Handler(database, authMw)
}
