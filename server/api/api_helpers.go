package api

import (
	"regexp"
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type goProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        goProxyPans `json:"pans"`
}

type goProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func normalizeGoProxyServers(value string) []goProxyServer {
	servers := mfnet.NormalizeGoProxyServers(value)
	out := make([]goProxyServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, goProxyServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			Pans:        goProxyPans{Baidu: s.Pans.Baidu, Quark: s.Pans.Quark},
		})
	}
	return out
}

var reNormalizeContentKey = regexp.MustCompile(`[\s\.\-_,，:：;；!！?？·•/\\|]+`)

func normalizeContentKey(s string) string {
	raw := strings.ToLower(strings.TrimSpace(s))
	if raw == "" {
		return ""
	}
	raw = reNormalizeContentKey.ReplaceAllString(raw, "")
	raw = strings.ReplaceAll(raw, "\u200b", "")
	raw = strings.ReplaceAll(raw, "\u200c", "")
	raw = strings.ReplaceAll(raw, "\u200d", "")
	raw = strings.ReplaceAll(raw, "\ufeff", "")
	return strings.TrimSpace(raw)
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func normalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
}
