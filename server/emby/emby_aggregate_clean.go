package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyLoadAggregateCleanRules(database *db.DB) []string {
	return smart.LoadAggregateCleanRules(database)
}

func embyAggKeyWithRules(text string, rawRules []string) string {
	return smart.AggKeyWithRules(text, rawRules)
}
