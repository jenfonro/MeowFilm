package emby

import "strings"

func embySplitPathParts(path string) []string {
	trimmed := strings.Trim(path, "/")
	if trimmed == "" {
		return []string{}
	}
	return strings.Split(trimmed, "/")
}

func embyHeadTail(parts []string) (head string, tail []string) {
	if len(parts) == 0 {
		return "", nil
	}
	return strings.ToLower(strings.TrimSpace(parts[0])), parts[1:]
}
