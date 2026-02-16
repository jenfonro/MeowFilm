package magic

import "strings"

type jsRule struct {
	Pattern string `json:"pattern"`
	Flags   string `json:"flags"`
	Replace string `json:"replace"`
}

func jsDecodeRule(raw string, forClean bool) jsRule {
	r := DecodeRule(raw, forClean)
	return jsRule{
		Pattern: strings.TrimSpace(r.Pattern),
		Flags:   strings.TrimSpace(r.Flags),
		Replace: strings.TrimSpace(r.Replace),
	}
}
