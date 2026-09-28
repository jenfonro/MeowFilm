package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

type embyTopologyContext struct {
	Current  *embyCurrentUser
	ServerID string
	Sections []db.ThirdPartyClientHomeSection
}

func ensureEmbyServerID(database *db.DB) (string, bool) {
	if database == nil {
		return "", false
	}
	serverID, err := database.EnsureServerIdentity()
	if err != nil || strings.TrimSpace(serverID) == "" {
		return "", false
	}
	return strings.TrimSpace(serverID), true
}

func resolveCurrentUserAndServerID(w http.ResponseWriter, r *http.Request, database *db.DB) (*embyCurrentUser, string, bool) {
	writeEmbyCommonHeaders(w.Header())
	current, status, message := resolveEmbyCurrentUser(database, r)
	if current == nil {
		writeEmbyAuthError(w, status, message)
		return nil, "", false
	}
	serverID, ok := ensureEmbyServerID(database)
	if !ok {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return nil, "", false
	}
	return current, serverID, true
}

func requireProtocolUserMatch(w http.ResponseWriter, current *embyCurrentUser, protocolUserID string) bool {
	if current == nil {
		return false
	}
	if !strings.EqualFold(strings.TrimSpace(protocolUserID), current.ProtocolUserID) {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return false
	}
	return true
}

func requireQueryUserMatch(w http.ResponseWriter, r *http.Request, current *embyCurrentUser) bool {
	if current == nil || r == nil {
		return false
	}
	if rawUserID := strings.TrimSpace(r.URL.Query().Get("UserId")); rawUserID != "" && !strings.EqualFold(rawUserID, current.ProtocolUserID) {
		writeEmbyError(w, http.StatusNotFound, "Not Found")
		return false
	}
	return true
}

func optionalCurrentUserID(database *db.DB, r *http.Request) int64 {
	current, _, _ := resolveEmbyCurrentUser(database, r)
	if current == nil {
		return 0
	}
	return current.Row.ID
}

func listEmbyMediaLibrarySections(database *db.DB) ([]db.ThirdPartyClientHomeSection, error) {
	sections, err := emby_service.ListSections(database)
	if err != nil {
		return nil, err
	}
	items := make([]db.ThirdPartyClientHomeSection, 0, len(sections))
	for _, section := range sections {
		if !emby_service.IsMediaLibrarySection(section) {
			continue
		}
		items = append(items, section)
	}
	return items, nil
}

func resolveTopologyContext(w http.ResponseWriter, r *http.Request, database *db.DB) (*embyTopologyContext, bool) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return nil, false
	}
	sections, err := listEmbyMediaLibrarySections(database)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return nil, false
	}
	return &embyTopologyContext{
		Current:  current,
		ServerID: serverID,
		Sections: sections,
	}, true
}

func listEmbyCollectionFolderItems(database *db.DB, serverID string) ([]emby_service.CollectionFolderItemDTO, error) {
	sections, err := listEmbyMediaLibrarySections(database)
	if err != nil {
		return nil, err
	}
	items := make([]emby_service.CollectionFolderItemDTO, 0, len(sections))
	for _, section := range sections {
		items = append(items, emby_service.BuildCollectionFolderItem(serverID, section))
	}
	return items, nil
}
