package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
)

type embyPublicMetaContext struct {
	ServerID string
	BaseURL  string
}

type embyBootstrapMetaContext struct {
	Current    *embyCurrentUser
	ServerID   string
	BaseURL    string
	ClientMeta clientmeta.RequestClientMeta
}

func resolvePublicMetaContext(database *db.DB, r *http.Request) (*embyPublicMetaContext, bool) {
	serverID, ok := ensureEmbyServerID(database)
	if !ok {
		return nil, false
	}
	return &embyPublicMetaContext{
		ServerID: serverID,
		BaseURL:  embyRequestBaseURL(r),
	}, true
}

func resolveBootstrapMetaContext(w http.ResponseWriter, r *http.Request, database *db.DB) (*embyBootstrapMetaContext, bool) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return nil, false
	}
	return &embyBootstrapMetaContext{
		Current:    current,
		ServerID:   serverID,
		BaseURL:    embyRequestBaseURL(r),
		ClientMeta: clientmeta.ResolveRequestClientMeta(r),
	}, true
}
