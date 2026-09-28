package emby

import (
	"net/http"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleUserProfile(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string) {
	ctx, ok := resolveBootstrapMetaContext(w, r, database)
	if !ok || ctx == nil {
		return
	}
	if !requireProtocolUserMatch(w, ctx.Current, protocolUserID) {
		return
	}
	writeJSON(w, http.StatusOK, buildEmbyAuthUser(ctx.Current.Row, ctx.ServerID, ctx.Current.ProtocolUserID, time.Now().UTC()))
}
