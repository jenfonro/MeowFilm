package magic

import (
	"encoding/json"
	"regexp"
	"strings"
	"unicode"
)

func marshalJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}

func parseJSONStringArray(text string) []string {
	var arr []any
	if err := json.Unmarshal([]byte(text), &arr); err != nil {
		return []string{}
	}
	out := make([]string, 0, len(arr))
	seen := map[string]struct{}{}
	for _, v := range arr {
		s, ok := v.(string)
		if !ok {
			continue
		}
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func intFromDigits(s string) int {
	n := 0
	for _, ch := range strings.TrimSpace(s) {
		if ch < '0' || ch > '9' {
			return 0
		}
		n = n*10 + int(ch-'0')
	}
	return n
}

func NormalizeForMatch(s string) string {
	in := strings.ToLower(strings.TrimSpace(s))
	if in == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(in))
	for _, r := range in {
		if unicode.IsSpace(r) || r == '\u200b' || r == '\u200c' || r == '\u200d' || r == '\ufeff' {
			continue
		}
		b.WriteRune(r)
	}
	return strings.TrimSpace(b.String())
}

var reReplaceTemplate = regexp.MustCompile(`\\(\d+)`)
