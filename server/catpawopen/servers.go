package catpawopen

import (
	"encoding/json"
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func ParseServers(raw string) []Server {
	var out []Server
	_ = json.Unmarshal([]byte(defaultString(strings.TrimSpace(raw), "[]")), &out)

	clean := make([]Server, 0, len(out))
	seen := map[string]struct{}{}
	for _, it := range out {
		n := strings.TrimSpace(it.Name)
		a := NormalizeAPIBase(it.APIBase)
		if n == "" || a == "" {
			continue
		}
		if _, ok := seen[n]; ok {
			continue
		}
		seen[n] = struct{}{}
		clean = append(clean, Server{Name: n, APIBase: a})
	}
	return clean
}

func PickActiveName(servers []Server, desired string) string {
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

func ResolveActiveBase(servers []Server, activeName string) string {
	k := PickActiveName(servers, activeName)
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

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}
