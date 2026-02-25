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

func embyFieldsHasCI(fieldsParam string, name string) bool {
	if strings.TrimSpace(fieldsParam) == "" || strings.TrimSpace(name) == "" {
		return false
	}
	for _, part := range strings.Split(fieldsParam, ",") {
		if strings.EqualFold(strings.TrimSpace(part), strings.TrimSpace(name)) {
			return true
		}
	}
	return false
}
