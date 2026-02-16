package emby

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
)

func embyAnyToString(v any) string {
	switch vv := v.(type) {
	case string:
		return vv
	case json.Number:
		return vv.String()
	case float64:
		return fmt.Sprintf("%.0f", vv)
	default:
		return ""
	}
}

func embyResolveCatApiBaseForUser(database *db.DB, u *embyUser) string {
	if database == nil {
		return ""
	}
	var userBase string
	if u != nil && strings.TrimSpace(u.Username) != "" {
		_ = database.SQL().QueryRow(`SELECT cat_api_base FROM users WHERE username=? LIMIT 1`, strings.TrimSpace(u.Username)).Scan(&userBase)
	}
	userBase = strings.TrimSpace(userBase)
	serverBase := strings.TrimSpace(catpawopen.ResolveActiveBase(catpawopen.ParseServers(database.GetSetting("catpawopen_servers")), database.GetSetting("catpawopen_active")))
	if u != nil && strings.TrimSpace(u.Role) == "user" {
		return strings.TrimSpace(userBase)
	}
	if userBase != "" {
		return userBase
	}
	return serverBase
}
