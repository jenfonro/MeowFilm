package routes

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/api"
)

func APIHandler(database *db.DB, authMw *auth.Auth) http.Handler {
	return api.Handler(database, authMw)
}

