package emby

import (
	"encoding/json"
	"fmt"

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
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListCatPawOpenServers()
	servers := make([]catpawopen.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawopen.Server{Name: s.Name, APIBase: s.APIBase})
	}
	_ = u
	return catpawopen.ResolveActiveBase(servers, cfg.CatPawOpenActive)
}
