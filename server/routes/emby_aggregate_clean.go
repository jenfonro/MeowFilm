package routes

import (
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

var embyCleanSpace = regexp.MustCompile(`\s+`)

func embyCompileCleanRegexRules(raw []string) []*regexp.Regexp {
	out := make([]*regexp.Regexp, 0, len(raw))
	for _, row := range raw {
		pat, _, fl := smartDecodeEpisodeRule(row)
		pat = strings.TrimSpace(pat)
		fl = strings.TrimSpace(fl)
		if pat == "" {
			pat = strings.TrimSpace(row)
		}
		if pat == "" {
			continue
		}
		if fl == "" {
			fl = "i"
		}
		re, err := embyCompileRegexp(pat, fl)
		if err != nil || re == nil {
			continue
		}
		out = append(out, re)
	}
	return out
}

func embyCompileRegexp(pat string, flags string) (*regexp.Regexp, error) {
	p := strings.TrimSpace(pat)
	if p == "" {
		return nil, nil
	}
	f := strings.TrimSpace(flags)
	prefix := ""
	if strings.Contains(f, "i") {
		prefix += "(?i)"
	}
	if strings.Contains(f, "m") {
		prefix += "(?m)"
	}
	if strings.Contains(f, "s") {
		prefix += "(?s)"
	}
	return regexp.Compile(prefix + p)
}

func embyApplyCleanRegexRules(text string, rules []*regexp.Regexp) string {
	out := strings.TrimSpace(text)
	if out == "" || len(rules) == 0 {
		return out
	}
	for _, re := range rules {
		if re == nil {
			continue
		}
		out = re.ReplaceAllString(out, "")
	}
	out = strings.TrimSpace(embyCleanSpace.ReplaceAllString(out, " "))
	return out
}

func embyAggKeyWithRules(text string, rules []*regexp.Regexp) string {
	cleaned := embyApplyCleanRegexRules(text, rules)
	return embyNormalizeAggKey(cleaned)
}

func embyCompileAggregateCleanRules(database *db.DB) []*regexp.Regexp {
	if database == nil {
		return nil
	}
	raw := parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules"))
	return embyCompileCleanRegexRules(raw)
}
