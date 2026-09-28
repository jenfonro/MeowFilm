package magic

import (
	"regexp"
	"strings"
	"unicode"

	"github.com/jenfonro/meowfilm/server/net"
)

func marshalJSON(v any) string {
	return net.MarshalJSON(v)
}

func parseJSONStringArray(text string) []string {
	return net.ParseJSONStringArray(text)
}

func minInt(a, b int) int {
	return net.MinInt(a, b)
}

func maxInt(a, b int) int {
	return net.MaxInt(a, b)
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
