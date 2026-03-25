package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleDisplayPreferencesUserSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	ctx, ok := resolveBootstrapMetaContext(w, r, database)
	if !ok || ctx == nil {
		return
	}
	if !requireQueryUserMatch(w, r, ctx.Current) {
		return
	}

	protocolUserID := ctx.Current.ProtocolUserID
	client := strings.TrimSpace(r.URL.Query().Get("client"))
	id := emby_service.StableMD5Hex("displaypreferences|" + protocolUserID + "|" + client + "|usersettings")

	writeJSON(w, http.StatusOK, embyDisplayPreferencesUserSettingsDTO{
		ID:          id,
		CustomPrefs: map[string]any{},
		SortOrder:   "Ascending",
		Client:      client,
	})
}
