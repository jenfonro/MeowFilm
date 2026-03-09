package smart

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/magic"
)

func embyLoadAggregateCleanRules(database *db.DB) []string {
	if database == nil {
		return nil
	}
	raw, _ := database.ListMagicAggregateRegexRules()
	if len(raw) == 0 {
		return nil
	}
	return raw
}

func embyAggKeyWithRules(text string, rawRules []string) string {
	in := strings.TrimSpace(text)
	if in == "" {
		return ""
	}
	cleaned := in
	if len(rawRules) > 0 {
		if out, err := magic.MagicAggregateNormalize(in, rawRules); err == nil {
			cleaned = strings.TrimSpace(out)
		}
	}
	return smartNormalizeAggKey(cleaned)
}

func smartLoadAggregateCleanRules(database *db.DB) []string {
	return embyLoadAggregateCleanRules(database)
}

func smartAggKeyWithRules(text string, rawRules []string) string {
	return embyAggKeyWithRules(text, rawRules)
}
