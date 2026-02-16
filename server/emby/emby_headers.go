package emby

import (
	"net/http"
	"strings"
)

func embyHeaderTrim(r *http.Request, key string) string {
	if r == nil {
		return ""
	}
	return strings.TrimSpace(r.Header.Get(key))
}
