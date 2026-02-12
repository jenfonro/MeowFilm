package routes

import (
	"net/http"
	"strings"
)

func jellyfinQueryGetCI(r *http.Request, key string) string {
	if r == nil {
		return ""
	}
	k := strings.TrimSpace(key)
	if k == "" {
		return ""
	}
	if v := strings.TrimSpace(r.URL.Query().Get(k)); v != "" {
		return v
	}
	q := r.URL.Query()
	for kk, vv := range q {
		if !strings.EqualFold(kk, k) {
			continue
		}
		if len(vv) == 0 {
			return ""
		}
		return strings.TrimSpace(vv[0])
	}
	return ""
}
