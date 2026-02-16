package emby

import (
	"net/http"
	"strconv"
	"strings"
)

func embyQueryGetCI(r *http.Request, key string) string {
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

func embyQueryTrimCI(r *http.Request, key string) string {
	return strings.TrimSpace(embyQueryGetCI(r, key))
}

func embyQueryIntClamped(r *http.Request, key string, def int, min int, max int) int {
	n, err := strconv.Atoi(embyQueryTrimCI(r, key))
	if err != nil {
		n = def
	}
	if n < min {
		return min
	}
	if max >= min && n > max {
		return max
	}
	return n
}
