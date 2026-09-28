package smart

import (
	"encoding/json"
	"fmt"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func smartAnyToString(v any) string {
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

func smartResolveCatApiBaseForUser(database *db.DB, u *SmartUser) string {
	if database == nil {
		return ""
	}
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListcatpawrunnerServers()
	servers := make([]catpawrunner.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
	}
	_ = u
	return catpawrunner.ResolveActiveBase(servers, cfg.CatpawrunnerActive)
}
