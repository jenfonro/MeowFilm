package routes

import (
	"encoding/json"
	"strings"
)

type catPawOpenServer struct {
	Name    string `json:"name"`
	APIBase string `json:"apiBase"`
}

func parseCatPawOpenServers(raw string) []catPawOpenServer {
	var out []catPawOpenServer
	_ = json.Unmarshal([]byte(defaultString(strings.TrimSpace(raw), "[]")), &out)

	clean := make([]catPawOpenServer, 0, len(out))
	seen := map[string]struct{}{}
	for _, it := range out {
		n := strings.TrimSpace(it.Name)
		a := normalizeCatPawOpenAPIBase(it.APIBase)
		if n == "" || a == "" {
			continue
		}
		if _, ok := seen[n]; ok {
			continue
		}
		seen[n] = struct{}{}
		clean = append(clean, catPawOpenServer{Name: n, APIBase: a})
	}
	return clean
}

func pickCatPawOpenActiveName(servers []catPawOpenServer, desired string) string {
	k := strings.TrimSpace(desired)
	if k != "" {
		for _, s := range servers {
			if s.Name == k {
				return s.Name
			}
		}
	}
	if len(servers) > 0 {
		return servers[0].Name
	}
	return ""
}

func resolveCatPawOpenActiveBase(servers []catPawOpenServer, activeName string) string {
	k := pickCatPawOpenActiveName(servers, activeName)
	if k == "" {
		return ""
	}
	for _, s := range servers {
		if s.Name == k {
			return s.APIBase
		}
	}
	return ""
}
