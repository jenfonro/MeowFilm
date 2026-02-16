package emby

import "strings"

type jsRule struct {
	Pattern string `json:"pattern"`
	Flags   string `json:"flags"`
	Replace string `json:"replace"`
}

func jsDecodeRule(raw string, forClean bool) jsRule {
	pat, rep, flags := smartDecodeEpisodeRule(raw)
	pat = strings.TrimSpace(pat)
	flags = strings.TrimSpace(flags)
	rep = strings.TrimSpace(smartNormalizeReplaceTemplate(rep))
	if flags == "" {
		flags = "i"
	}
	if forClean {
		// clean rules ignore replace template in our settings (remove matches).
		rep = ""
	}
	return jsRule{Pattern: pat, Flags: flags, Replace: rep}
}
