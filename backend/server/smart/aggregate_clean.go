package smart

import (
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/magic"
)

var (
	smartRePlainCNSeasonSuffix = regexp.MustCompile(`(?i)\s*[（(\[]?\s*(?:\d+|[一二三四五六七八九十百零〇两]+)\s*季\s*[）)\]]?\s*$`)
	smartReYearBangSuffix      = regexp.MustCompile(`(?i)\s*[（(\[]?\s*年番\s*(?:\d+|[一二三四五六七八九十百零〇两]+)\s*[）)\]]?\s*$`)
)

func smartCanonicalAggregateTitle(text string) string {
	s := strings.TrimSpace(text)
	if s == "" {
		return ""
	}
	orig := s
	s = smartReCNSeasonSuffix.ReplaceAllString(s, "")
	s = smartRePlainCNSeasonSuffix.ReplaceAllString(s, "")
	s = smartReENSeasonSuffix.ReplaceAllString(s, "")
	s = smartReSSeasonSuffix.ReplaceAllString(s, "")
	s = smartReYearBangSuffix.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	if s == "" {
		return strings.TrimSpace(orig)
	}
	return s
}

func loadAggregateCleanRules(database *db.DB) []string {
	if database == nil {
		return nil
	}
	raw, _ := database.ListMagicAggregateRegexRules()
	if len(raw) == 0 {
		return nil
	}
	return raw
}

func aggregateKeyWithRules(text string, rawRules []string) string {
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
	cleaned = smartCanonicalAggregateTitle(cleaned)
	return smartNormalizeAggKey(cleaned)
}

func smartLoadAggregateCleanRules(database *db.DB) []string {
	return loadAggregateCleanRules(database)
}

func smartAggKeyWithRules(text string, rawRules []string) string {
	return aggregateKeyWithRules(text, rawRules)
}
