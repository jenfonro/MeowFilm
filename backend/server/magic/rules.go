package magic

import (
	"encoding/json"
	"strings"
)

// DecodeEpisodeRule parses a rule row from settings and returns pattern/replace/flags.
// Supported formats:
// - JSON object string: {"pattern":"...","replace":"...","flags":"i"}
// - /pattern/flags
// - plain pattern
func DecodeEpisodeRule(raw string) (pattern string, replace string, flags string) {
	s := strings.TrimSpace(raw)
	if s == "" {
		return "", "", ""
	}
	if strings.HasPrefix(s, "{") && strings.HasSuffix(s, "}") {
		var obj struct {
			Pattern string `json:"pattern"`
			Replace string `json:"replace"`
			Flags   string `json:"flags"`
		}
		if err := json.Unmarshal([]byte(s), &obj); err == nil && strings.TrimSpace(obj.Pattern) != "" {
			return strings.TrimSpace(obj.Pattern), obj.Replace, strings.TrimSpace(obj.Flags)
		}
	}
	if strings.HasPrefix(s, "/") {
		last := strings.LastIndex(s, "/")
		if last > 0 {
			p := strings.TrimSpace(s[1:last])
			f := strings.TrimSpace(s[last+1:])
			if p != "" {
				return p, "", f
			}
		}
	}
	return s, "", ""
}

func NormalizeReplaceTemplate(replaceRaw string) string {
	if replaceRaw == "" {
		return ""
	}
	return reReplaceTemplate.ReplaceAllString(replaceRaw, `$$$1`)
}

func DecodeRule(raw string, forClean bool) Rule {
	pat, rep, flags := DecodeEpisodeRule(raw)
	pat = strings.TrimSpace(pat)
	flags = strings.TrimSpace(flags)
	rep = strings.TrimSpace(NormalizeReplaceTemplate(rep))
	if flags == "" {
		flags = "i"
	}
	if forClean {
		rep = ""
	}
	return Rule{Pattern: pat, Flags: flags, Replace: rep}
}
