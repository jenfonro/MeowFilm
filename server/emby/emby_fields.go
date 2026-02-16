package emby

import "strings"

func embyFieldsSet(fieldsParam string) map[string]struct{} {
	out := map[string]struct{}{}
	for _, part := range strings.Split(fieldsParam, ",") {
		p := strings.TrimSpace(part)
		if p != "" {
			out[p] = struct{}{}
		}
	}
	return out
}
