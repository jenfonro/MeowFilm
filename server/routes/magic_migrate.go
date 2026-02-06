package routes

import (
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func splitAggregateKeywordTokens(input string) []string {
	raw := strings.TrimSpace(input)
	if raw == "" {
		return nil
	}
	out := []string{}
	parts := strings.FieldsFunc(raw, func(r rune) bool {
		switch r {
		case ' ', '\t', '\n', '\r', ',', '，', '、', ';', '；':
			return true
		default:
			return false
		}
	})
	for _, p := range parts {
		t := strings.TrimSpace(p)
		if t == "" {
			continue
		}
		out = append(out, t)
	}
	return out
}

func normalizeRegexRulePattern(rule string) string {
	s := strings.TrimSpace(rule)
	if s == "" {
		return ""
	}
	if strings.HasPrefix(s, "/") {
		last := strings.LastIndex(s, "/")
		if last > 0 {
			return strings.TrimSpace(s[1:last])
		}
	}
	return s
}

// migrateMagicAggregateKeywordRulesToRegex converts legacy "magic_aggregate_rules" (keyword list)
// into escaped regex patterns stored in "magic_aggregate_regex_rules", then clears the legacy setting.
func migrateMagicAggregateKeywordRulesToRegex(database *db.DB) (changed bool) {
	legacy := parseJSONStringArray(database.GetSetting("magic_aggregate_rules"))
	if len(legacy) == 0 {
		return false
	}

	existingRaw := parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules"))
	existing := make([]string, 0, len(existingRaw))
	existingSet := map[string]struct{}{}
	for _, it := range existingRaw {
		r := strings.TrimSpace(it)
		if r == "" {
			continue
		}
		existing = append(existing, r)
		p := normalizeRegexRulePattern(r)
		if p != "" {
			existingSet[strings.ToLower(p)] = struct{}{}
		}
	}

	added := 0
	for _, row := range legacy {
		for _, token := range splitAggregateKeywordTokens(row) {
			pattern := regexp.QuoteMeta(token)
			key := strings.ToLower(pattern)
			if _, ok := existingSet[key]; ok {
				continue
			}
			existingSet[key] = struct{}{}
			existing = append(existing, pattern)
			added += 1
		}
	}

	_ = database.SetSetting("magic_aggregate_regex_rules", marshalJSON(existing))
	_ = database.SetSetting("magic_aggregate_rules", "[]")
	return added > 0 || len(legacy) > 0
}

